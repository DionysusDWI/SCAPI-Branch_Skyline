using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.60：**表面体素壳的网格化**（六向贪心）—— 把"裸露体素"变成能画的几何，
    /// 这就是用户要的"**体积感**"（台阶、屋檐、树冠、雪层不再被压平）。
    ///
    /// 与 `CubeSurface32Mesh`（列顶高度场，2D 贪心）的区别：
    ///   * 输入是 <see cref="SurfaceVoxelShell32"/>（占用位图 + 稀疏体素材质）；
    ///   * **逐朝向**（+Y/-Y/+X/-X/+Z/-Z）做 **2D 贪心**：同一片、同一朝向、同一材质的相邻面并成一个四边形；
    ///   * 一个面的可见性 = 该侧的邻居**不实心**（邻居在立方体外 → 按可见处理；
    ///     相邻立方体会在同一个平面上画它自己的面，靠背面剔除互相隐藏）。
    ///
    /// 顶点仍是 `TerrainVertex`（position + texcoord + color）→ **可以直接用游戏的地形 shader 画**，
    /// 所以本步不需要改顶点格式/写着色器（法线与材质 id 是目标 1.3 的后续步骤）。
    /// </summary>
    public sealed class SurfaceVoxelMesh {
        public const int Size = SurfaceVoxelShell32.Size;

        public int Cx, Cy, Cz;
        public int NaiveFaces;                 // 裸露面总数（= 朴素口径的四边形数）
        public int TopQuads, BottomQuads, XQuads, ZQuads;
        /// <summary>实际生成的四边形数（由 `EmitFace` 递增；**不是**把四个方向加起来 —— 那四个是分类统计）。</summary>
        public int Quads { get; private set; }
        public int Vertices;
        public int IndexCount;
        public long VertexBytes => (long)Vertices * 20;
        public double BuildMs;
        public int UsedVoxels;
        public bool Degraded;

        public VertexBuffer VertexBuffer;
        public IndexBuffer IndexBuffer;

        TerrainVertex[] m_vertexData = new TerrainVertex[4096];
        int[] m_indexData = new int[6144];
        int m_vertexCount, m_indexCount;

        public static SurfaceVoxelMesh Build(SurfaceVoxelShell32 shell, bool greedy = true, bool withBuffers = true) {
            Stopwatch watch = Stopwatch.StartNew();
            SurfaceVoxelMesh mesh = new() {
                Cx = shell.X,
                Cy = shell.Y,
                Cz = shell.Z,
                UsedVoxels = shell.VoxelCount,
                Degraded = shell.Degraded
            };
            if (shell.VoxelCount == 0) {
                mesh.BuildMs = watch.Elapsed.TotalMilliseconds;
                return mesh;
            }
            // 1) 面可见表：6 个朝向各一张 32³ 位图（该体素在该朝向"有可见面"且材质）
            //    材质按"体素 → 材质"存一份（面表只存可见性 + 复用体素材质）
            ushort[] material = new ushort[SurfaceVoxelShell32.CellCount];
            for (int i = 0; i < shell.VoxelCount; i++) {
                material[shell.IndexAt(i)] = (ushort)shell.MaterialAt(i);
            }
            int[] dx = [0, 0, 1, -1, 0, 0];
            int[] dy = [1, -1, 0, 0, 0, 0];
            int[] dz = [0, 0, 0, 0, 1, -1];
            for (int f = 0; f < 6; f++) {
                List<(int X, int Y, int Z, int W, int H)> quads = [];
                bool[] used = new bool[SurfaceVoxelShell32.CellCount];
                for (int y = 0; y < Size; y++) {
                    for (int z = 0; z < Size; z++) {
                        for (int x = 0; x < Size; x++) {
                            int index = SurfaceVoxelShell32.PackIndex(x, y, z);
                            if (used[index] || !Visible(shell, material, x, y, z, f, dx, dy, dz, out int mat)) {
                                continue;
                            }
                            // 沿 +X、再沿 +Z 扩（与列顶网格同一套贪心）
                            int w = 1;
                            while (x + w < Size) {
                                int j = SurfaceVoxelShell32.PackIndex(x + w, y, z);
                                if (used[j] || !Visible(shell, material, x + w, y, z, f, dx, dy, dz, out int mat2)
                                    || mat2 != mat) {
                                    break;
                                }
                                w++;
                            }
                            int h = 1;
                            while (z + h < Size) {
                                bool rowOk = true;
                                for (int k = 0; k < w; k++) {
                                    int j = SurfaceVoxelShell32.PackIndex(x + k, y, z + h);
                                    if (used[j] || !Visible(shell, material, x + k, y, z + h, f, dx, dy, dz, out int mat3)
                                        || mat3 != mat) {
                                        rowOk = false;
                                        break;
                                    }
                                }
                                if (!rowOk) {
                                    break;
                                }
                                h++;
                            }
                            for (int zz = z; zz < z + h; zz++) {
                                for (int xx = x; xx < x + w; xx++) {
                                    used[SurfaceVoxelShell32.PackIndex(xx, y, zz)] = true;
                                }
                            }
                            quads.Add((x, y, z, w, h));
                        }
                    }
                }
                int before = mesh.Quads;
                foreach ((int X, int Y, int Z, int W, int H) q in quads) {
                    int index = SurfaceVoxelShell32.PackIndex(q.X, q.Y, q.Z);
                    mesh.EmitFace(f, q.X, q.Y, q.Z, q.W, q.H, material[index]);
                }
                switch (f) {
                    case 0: mesh.TopQuads = mesh.Quads - before; break;
                    case 1: mesh.BottomQuads = mesh.Quads - before; break;
                    case 2:
                    case 3: mesh.XQuads += mesh.Quads - before; break;
                    default: mesh.ZQuads += mesh.Quads - before; break;
                }
            }
            mesh.NaiveFaces = CountNaiveFaces(shell, material, dx, dy, dz);
            if (withBuffers && mesh.m_vertexCount > 0) {
                mesh.CreateBuffers();
            }
            mesh.BuildMs = watch.Elapsed.TotalMilliseconds;
            return mesh;
        }

        static int CountNaiveFaces(SurfaceVoxelShell32 shell, ushort[] material, int[] dx, int[] dy, int[] dz) {
            int n = 0;
            for (int y = 0; y < Size; y++) {
                for (int z = 0; z < Size; z++) {
                    for (int x = 0; x < Size; x++) {
                        for (int f = 0; f < 6; f++) {
                            if (Visible(shell, material, x, y, z, f, dx, dy, dz, out _)) {
                                n++;
                            }
                        }
                    }
                }
            }
            return n;
        }

        /// <summary>该体素在朝向 f 上是否有可见面（= 该体素裸露 且 邻居不实心）。</summary>
        static bool Visible(SurfaceVoxelShell32 shell, ushort[] material, int x, int y, int z, int f,
                            int[] dx, int[] dy, int[] dz, out int mat) {
            mat = 0;
            int index = SurfaceVoxelShell32.PackIndex(x, y, z);
            if (material[index] == 0) {
                return false;                                     // 不是裸露体素
            }
            int nx = x + dx[f], ny = y + dy[f], nz = z + dz[f];
            if (nx >= 0 && nx < Size && ny >= 0 && ny < Size && nz >= 0 && nz < Size
                && shell.IsSolid(nx, ny, nz)) {
                return false;                                     // 邻居实心 → 这个面看不到
            }
            mat = material[index];
            return true;
        }

        void EmitFace(int f, int x, int y, int z, int w, int h, int value) {
            Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
            int slotCount = Math.Max(block.GetTextureSlotCount(value), 1);
            int slot = SkylineRuntime.LodMaterialAware
                ? SkylineLod.MaterialTextureSlot(block, value)
                : block.GetFaceTextureSlot(4, value);
            float u0 = (slot % slotCount) / (float)slotCount;
            float v0 = (slot / slotCount) / (float)slotCount;
            float du = 1f / slotCount;
            int light = Terrain.ExtractLight(value);
            byte b = (byte)(light * 17);
            Color color = new(b, b, b);
            float wx = Cx * Size + x, wy = Cy * Size + y, wz = Cz * Size + z;
            int vslot = Reserve(4);
            switch (f) {
                case 0: {   // +Y（顶面）
                    float y1 = wy + 1f, x1 = wx + w, z1 = wz + h;
                    BlockGeometryGenerator.SetupVertex(wx, y1, wz, color, u0, v0, ref m_vertexData[vslot]);
                    BlockGeometryGenerator.SetupVertex(x1, y1, wz, color, u0 + du, v0, ref m_vertexData[vslot + 1]);
                    BlockGeometryGenerator.SetupVertex(x1, y1, z1, color, u0 + du, v0 + du, ref m_vertexData[vslot + 2]);
                    BlockGeometryGenerator.SetupVertex(wx, y1, z1, color, u0, v0 + du, ref m_vertexData[vslot + 3]);
                    break;
                }
                case 1: {   // -Y（底面）
                    float y0 = wy, x1 = wx + w, z1 = wz + h;
                    BlockGeometryGenerator.SetupVertex(wx, y0, z1, color, u0, v0, ref m_vertexData[vslot]);
                    BlockGeometryGenerator.SetupVertex(x1, y0, z1, color, u0 + du, v0, ref m_vertexData[vslot + 1]);
                    BlockGeometryGenerator.SetupVertex(x1, y0, wz, color, u0 + du, v0 + du, ref m_vertexData[vslot + 2]);
                    BlockGeometryGenerator.SetupVertex(wx, y0, wz, color, u0, v0 + du, ref m_vertexData[vslot + 3]);
                    break;
                }
                case 2: {   // +X（x = wx+1，face 在 y/z 上铺 h×w）
                    float x1 = wx + 1f, z1 = wz + w, y1 = wy + h;
                    BlockGeometryGenerator.SetupVertex(x1, wy, wz, color, u0, v0, ref m_vertexData[vslot]);
                    BlockGeometryGenerator.SetupVertex(x1, wy, z1, color, u0 + du, v0, ref m_vertexData[vslot + 1]);
                    BlockGeometryGenerator.SetupVertex(x1, y1, z1, color, u0 + du, v0 + du, ref m_vertexData[vslot + 2]);
                    BlockGeometryGenerator.SetupVertex(x1, y1, wz, color, u0, v0 + du, ref m_vertexData[vslot + 3]);
                    break;
                }
                case 3: {   // -X
                    float x0 = wx, z1 = wz + w, y1 = wy + h;
                    BlockGeometryGenerator.SetupVertex(x0, wy, z1, color, u0, v0, ref m_vertexData[vslot]);
                    BlockGeometryGenerator.SetupVertex(x0, wy, wz, color, u0 + du, v0, ref m_vertexData[vslot + 1]);
                    BlockGeometryGenerator.SetupVertex(x0, y1, wz, color, u0 + du, v0 + du, ref m_vertexData[vslot + 2]);
                    BlockGeometryGenerator.SetupVertex(x0, y1, z1, color, u0, v0 + du, ref m_vertexData[vslot + 3]);
                    break;
                }
                case 4: {   // +Z
                    float z1 = wz + 1f, x1 = wx + w, y1 = wy + h;
                    BlockGeometryGenerator.SetupVertex(wx, wy, z1, color, u0, v0, ref m_vertexData[vslot]);
                    BlockGeometryGenerator.SetupVertex(x1, wy, z1, color, u0 + du, v0, ref m_vertexData[vslot + 1]);
                    BlockGeometryGenerator.SetupVertex(x1, y1, z1, color, u0 + du, v0 + du, ref m_vertexData[vslot + 2]);
                    BlockGeometryGenerator.SetupVertex(wx, y1, z1, color, u0, v0 + du, ref m_vertexData[vslot + 3]);
                    break;
                }
                default: {  // -Z
                    float z0 = wz, x1 = wx + w, y1 = wy + h;
                    BlockGeometryGenerator.SetupVertex(x1, wy, z0, color, u0, v0, ref m_vertexData[vslot]);
                    BlockGeometryGenerator.SetupVertex(wx, wy, z0, color, u0 + du, v0, ref m_vertexData[vslot + 1]);
                    BlockGeometryGenerator.SetupVertex(wx, y1, z0, color, u0 + du, v0 + du, ref m_vertexData[vslot + 2]);
                    BlockGeometryGenerator.SetupVertex(x1, y1, z0, color, u0, v0 + du, ref m_vertexData[vslot + 3]);
                    break;
                }
            }
            int v = vslot;
            Quads++;
            PushIndex(v);
            PushIndex(v + 1);
            PushIndex(v + 2);
            PushIndex(v);
            PushIndex(v + 2);
            PushIndex(v + 3);
            // 双面（与列顶网格同口径：从任意一侧看都不能穿）
            PushIndex(v + 2);
            PushIndex(v + 1);
            PushIndex(v);
            PushIndex(v + 3);
            PushIndex(v + 2);
            PushIndex(v);
        }

        int Reserve(int n) {
            if (m_vertexCount + n > m_vertexData.Length) {
                Array.Resize(ref m_vertexData, Math.Max(m_vertexData.Length * 2, m_vertexCount + n));
            }
            int start = m_vertexCount;
            m_vertexCount += n;
            return start;
        }

        void PushIndex(int value) {
            if (m_indexCount >= m_indexData.Length) {
                Array.Resize(ref m_indexData, m_indexData.Length * 2);
            }
            m_indexData[m_indexCount++] = value;
        }

        void CreateBuffers() {
            Vertices = m_vertexCount;
            IndexCount = m_indexCount;
            VertexBuffer = new VertexBuffer(TerrainVertex.VertexDeclaration, Vertices);
            VertexBuffer.SetData(m_vertexData, 0, Vertices);
            bool big = Vertices > 65535;
            IndexBuffer = new IndexBuffer(big ? IndexFormat.ThirtyTwoBits : IndexFormat.SixteenBits, IndexCount);
            if (big) {
                IndexBuffer.SetData(m_indexData, 0, IndexCount);
            }
            else {
                short[] shorts = new short[IndexCount];
                for (int i = 0; i < IndexCount; i++) {
                    shorts[i] = (short)m_indexData[i];
                }
                IndexBuffer.SetData(shorts, 0, IndexCount);
            }
        }

        public void Dispose() {
            Utilities.Dispose(ref VertexBuffer);
            Utilities.Dispose(ref IndexBuffer);
        }

        public JsonObject DescribeObject() {
            return new JsonObject {
                ["cube"] = new JsonArray(Cx, Cy, Cz),
                ["usedVoxels"] = UsedVoxels,
                ["degraded"] = Degraded,
                ["naiveFaces"] = NaiveFaces,
                ["quads"] = Quads,
                ["topQuads"] = TopQuads,
                ["bottomQuads"] = BottomQuads,
                ["xQuads"] = XQuads,
                ["zQuads"] = ZQuads,
                ["merged"] = NaiveFaces > 0 ? Math.Round(1.0 - Quads / (double)NaiveFaces, 3) : 0.0,
                ["vertices"] = Vertices,
                ["vertexBytes"] = VertexBytes,
                ["buildMs"] = Math.Round(BuildMs, 2)
            };
        }
    }

    /// <summary>桥：`skyline.SurfaceVoxelMesh(cx,cy,cz)`。</summary>
    public static partial class SkylineRuntime {
        /// <summary>抓表面体素壳 → 建六向贪心网格，报体素/面/四边形/字节（1.6 的几何验证）。</summary>
        public static string SurfaceVoxelMeshInfo(int cx, int cy, int cz) {
            JsonObject result = new();
            try {
                Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
                if (terrain == null) {
                    result["ok"] = false;
                    result["err"] = "no terrain";
                    return result.ToJsonString();
                }
                SurfaceVoxelShell32 shell = SurfaceVoxelShell32.ExtractFrom(terrain, cx, cy, cz);
                SurfaceVoxelMesh mesh = SurfaceVoxelMesh.Build(shell, true, true);
                CubeSurface32 heightShell = CubeSurface32.Extract(terrain, cx, cy, cz);
                CubeSurfaceMesh32 heightMesh = CubeSurfaceMesh32.Build([heightShell], 1, 1, true, true, 1);
                result["ok"] = true;
                result["voxelShell"] = shell.Describe();
                result["voxelMesh"] = mesh.DescribeObject();
                result["heightFieldMesh"] = heightMesh.DescribeObject();
                result["note"] = "对比：表面体素壳（六向贪心）vs 列顶高度场壳（2D 贪心）——"
                    + "前者能表达台阶/屋檐/树冠底面，代价是四边形与顶点字节更多";
                mesh.Dispose();          // 取证调用不留 GPU 资源
                heightMesh.Dispose();
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }
    }
}
