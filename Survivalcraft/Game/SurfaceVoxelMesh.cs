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
    /// v0.1.60 起**一次收集面、两种输出**：
    ///   * `attributes: false` → `TerrainVertex`（20 B），把 `SkylineFaceShading` 的面因子**烘焙**进顶点色，
    ///     用游戏的 `Opaque` 着色器画（这是"立刻有体积感"的路径）；
    ///   * `attributes: true`  → <see cref="SkylineLodVertex"/>（28 B，多出**法线 + 材质 id**），
    ///     顶点色只带原始光照，面明暗由 `SkylineLodVolume` 在片元里用同一条公式算（这是 Iris 侧要的路径）。
    ///   两套网格的**顶点位置与索引顺序逐位相同** → `skyline.LodVolumeCompare()` 可以逐像素对拍。
    /// </summary>
    public sealed class SurfaceVoxelMesh {
        public const int Size = SurfaceVoxelShell32.Size;

        /// <summary>顶点格式：true = `SkylineLodVertex`（带法线/材质 id），false = `TerrainVertex`（烘焙面因子）。</summary>
        public readonly bool HasAttributes;

        public int Cx, Cy, Cz;
        public int NaiveFaces;                 // 裸露面总数（= 朴素口径的四边形数）
        public int TopQuads, BottomQuads, XQuads, ZQuads;
        /// <summary>实际生成的四边形数（由 `EmitFace*` 递增；那四个分类字段是分类统计）。</summary>
        public int Quads { get; private set; }
        /// <summary>按**本类内部朝向**计数的四边形数：0=+Y 1=-Y 2=+X 3=-X 4=+Z 5=-Z。</summary>
        public readonly int[] QuadsByFace = new int[6];
        public int Vertices;
        public int IndexCount;
        /// <summary>属性格式下出现过的不同法线数（= 6 才说明六面都写了法线）。</summary>
        public int DistinctNormals;
        /// <summary>属性格式下出现过的不同材质 id 数（对拍用）。</summary>
        public int DistinctMaterials;
        public long VertexBytes => (long)Vertices * (HasAttributes ? SkylineLodVertex.Stride : 20);
        public double BuildMs;
        public int UsedVoxels;
        public bool Degraded;

        public VertexBuffer VertexBuffer;
        public IndexBuffer IndexBuffer;

        // 本类内部朝向 → 引擎 `CellFace` 编号（0=+Z 1=+X 2=-Z 3=-X 4=+Y 5=-Y）
        static readonly int[] s_cellFace = [4, 5, 1, 3, 0, 2];

        TerrainVertex[] m_vertexData = new TerrainVertex[4096];
        SkylineLodVertex[] m_attrData = new SkylineLodVertex[4096];
        int[] m_indexData = new int[6144];
        int m_vertexCount, m_indexCount;

        readonly struct Face {
            public readonly int Dir, X, Y, Z, W, H, Value;
            public Face(int dir, int x, int y, int z, int w, int h, int value) {
                Dir = dir; X = x; Y = y; Z = z; W = w; H = h; Value = value;
            }
        }

        SurfaceVoxelMesh(bool attributes) {
            HasAttributes = attributes;
        }

        /// <summary>
        /// 建壳网格。`attributes: true` 输出 <see cref="SkylineLodVertex"/>（法线 + 材质 id）。
        /// `withBuffers: false` 只算统计（取证调用用）。
        /// </summary>
        public static SurfaceVoxelMesh Build(SurfaceVoxelShell32 shell, bool greedy = true, bool withBuffers = true,
                                             bool attributes = false) {
            Stopwatch watch = Stopwatch.StartNew();
            SurfaceVoxelMesh mesh = new(attributes) {
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
            // 1) 每个裸露体素的材质（面表只存可见性，材质复用体素表）
            ushort[] material = new ushort[SurfaceVoxelShell32.CellCount];
            for (int i = 0; i < shell.VoxelCount; i++) {
                material[shell.IndexAt(i)] = (ushort)shell.MaterialAt(i);
            }
            int[] dx = [0, 0, 1, -1, 0, 0];
            int[] dy = [1, -1, 0, 0, 0, 0];
            int[] dz = [0, 0, 0, 0, 1, -1];
            List<Face> faces = [];
            for (int f = 0; f < 6; f++) {
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
                            faces.Add(new Face(f, x, y, z, w, h, mat));
                        }
                    }
                }
            }
            foreach (Face face in faces) {
                if (mesh.HasAttributes) {
                    mesh.EmitFaceAttributes(face);
                }
                else {
                    mesh.EmitFaceTerrain(face);
                }
            }
            mesh.NaiveFaces = CountNaiveFaces(shell, material, dx, dy, dz);
            if (mesh.HasAttributes) {
                mesh.DistinctNormals = CountDistinctNormals(faces);
                mesh.DistinctMaterials = CountDistinctMaterials(faces);
            }
            if (withBuffers && mesh.m_vertexCount > 0) {
                mesh.CreateBuffers();
            }
            mesh.BuildMs = watch.Elapsed.TotalMilliseconds;
            return mesh;
        }

        static int CountDistinctNormals(List<Face> faces) {
            bool[] seen = new bool[6];
            int n = 0;
            foreach (Face f in faces) {
                if (!seen[f.Dir]) {
                    seen[f.Dir] = true;
                    n++;
                }
            }
            return n;
        }

        static int CountDistinctMaterials(List<Face> faces) {
            HashSet<int> set = [];
            foreach (Face f in faces) {
                set.Add(f.Value);
            }
            return set.Count;
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

        // ---------------------------------------------------------------- 顶点写入

        /// <summary>一个贪心四边形的四个角（世界坐标），顺序与朝向有关（与旧实现逐位相同）。</summary>
        void QuadCorners(int f, int x, int y, int z, int w, int h,
                         out float ax, out float ay, out float az,
                         out float bx, out float by, out float bz,
                         out float cx, out float cy, out float cz,
                         out float dx, out float dy, out float dz) {
            float wx = Cx * Size + x, wy = Cy * Size + y, wz = Cz * Size + z;
            switch (f) {
                case 0: {   // +Y（顶面）
                    float y1 = wy + 1f, x1 = wx + w, z1 = wz + h;
                    ax = wx; ay = y1; az = wz;
                    bx = x1; by = y1; bz = wz;
                    cx = x1; cy = y1; cz = z1;
                    dx = wx; dy = y1; dz = z1;
                    break;
                }
                case 1: {   // -Y（底面）
                    float y0 = wy, x1 = wx + w, z1 = wz + h;
                    ax = wx; ay = y0; az = z1;
                    bx = x1; by = y0; bz = z1;
                    cx = x1; cy = y0; cz = wz;
                    dx = wx; dy = y0; dz = wz;
                    break;
                }
                case 2: {   // +X
                    float x1 = wx + 1f, z1 = wz + w, y1 = wy + h;
                    ax = x1; ay = wy; az = wz;
                    bx = x1; by = wy; bz = z1;
                    cx = x1; cy = y1; cz = z1;
                    dx = x1; dy = y1; dz = wz;
                    break;
                }
                case 3: {   // -X
                    float x0 = wx, z1 = wz + w, y1 = wy + h;
                    ax = x0; ay = wy; az = z1;
                    bx = x0; by = wy; bz = wz;
                    cx = x0; cy = y1; cz = wz;
                    dx = x0; dy = y1; dz = z1;
                    break;
                }
                case 4: {   // +Z
                    float z1 = wz + 1f, x1 = wx + w, y1 = wy + h;
                    ax = wx; ay = wy; az = z1;
                    bx = x1; by = wy; bz = z1;
                    cx = x1; cy = y1; cz = z1;
                    dx = wx; dy = y1; dz = z1;
                    break;
                }
                default: {  // -Z
                    float z0 = wz, x1 = wx + w, y1 = wy + h;
                    ax = x1; ay = wy; az = z0;
                    bx = wx; by = wy; bz = z0;
                    cx = wx; cy = y1; cz = z0;
                    dx = x1; dy = y1; dz = z0;
                    break;
                }
            }
        }

        /// <summary>老路径：`TerrainVertex` + 把面因子烘焙进顶点色（用游戏 Opaque 着色器画）。</summary>
        void EmitFaceTerrain(Face face) {
            Block block = BlocksManager.Blocks[Terrain.ExtractContents(face.Value)];
            int slotCount = Math.Max(block.GetTextureSlotCount(face.Value), 1);
            int slot = SkylineRuntime.LodMaterialAware
                ? SkylineLod.MaterialTextureSlot(block, face.Value)
                : block.GetFaceTextureSlot(4, face.Value);
            float u0 = (slot % slotCount) / (float)slotCount;
            float v0 = (slot / slotCount) / (float)slotCount;
            float du = 1f / slotCount;
            int light = Terrain.ExtractLight(face.Value);
            byte b = (byte)(light * 17);
            // [v0.1.60] 体积感：顶点色乘上**游戏自己的面因子**（顶面因子 = 1 → 顶面逐位不变）
            Color color = SkylineFaceShading.Apply(new Color(b, b, b), s_cellFace[face.Dir]);
            QuadCorners(face.Dir, face.X, face.Y, face.Z, face.W, face.H,
                out float ax, out float ay, out float az,
                out float bx, out float by, out float bz,
                out float cx, out float cy, out float cz,
                out float dx, out float dy, out float dz);
            int v = Reserve(4);
            BlockGeometryGenerator.SetupVertex(ax, ay, az, color, u0, v0, ref m_vertexData[v]);
            BlockGeometryGenerator.SetupVertex(bx, by, bz, color, u0 + du, v0, ref m_vertexData[v + 1]);
            BlockGeometryGenerator.SetupVertex(cx, cy, cz, color, u0 + du, v0 + du, ref m_vertexData[v + 2]);
            BlockGeometryGenerator.SetupVertex(dx, dy, dz, color, u0, v0 + du, ref m_vertexData[v + 3]);
            PushQuadIndices(v);
            CountQuad(face.Dir);
        }

        /// <summary>
        /// 新路径：`SkylineLodVertex` —— 顶点色只带**原始光照**（不烘焙面因子），
        /// 面明暗 + 材质判定交给 `SkylineLodVolume`（法线与材质 id 作为顶点属性过河）。
        /// </summary>
        void EmitFaceAttributes(Face face) {
            Block block = BlocksManager.Blocks[Terrain.ExtractContents(face.Value)];
            int slotCount = Math.Max(block.GetTextureSlotCount(face.Value), 1);
            int slot = SkylineRuntime.LodMaterialAware
                ? SkylineLod.MaterialTextureSlot(block, face.Value)
                : block.GetFaceTextureSlot(4, face.Value);
            float u0 = (slot % slotCount) / (float)slotCount;
            float v0 = (slot / slotCount) / (float)slotCount;
            float du = 1f / slotCount;
            int light = Terrain.ExtractLight(face.Value);
            byte b = (byte)(light * 17);
            Color color = new(b, b, b);
            Vector3 normal = SkylineLodVertex.FaceNormal(s_cellFace[face.Dir]);
            float materialId = face.Value;
            QuadCorners(face.Dir, face.X, face.Y, face.Z, face.W, face.H,
                out float ax, out float ay, out float az,
                out float bx, out float by, out float bz,
                out float cx, out float cy, out float cz,
                out float dx, out float dy, out float dz);
            int v = Reserve(4);
            SkylineLodVertex.Setup(ax, ay, az, color, u0, v0, normal, materialId, ref m_attrData[v]);
            SkylineLodVertex.Setup(bx, by, bz, color, u0 + du, v0, normal, materialId, ref m_attrData[v + 1]);
            SkylineLodVertex.Setup(cx, cy, cz, color, u0 + du, v0 + du, normal, materialId, ref m_attrData[v + 2]);
            SkylineLodVertex.Setup(dx, dy, dz, color, u0, v0 + du, normal, materialId, ref m_attrData[v + 3]);
            PushQuadIndices(v);
            CountQuad(face.Dir);
        }

        void CountQuad(int dir) {
            Quads++;
            QuadsByFace[dir]++;
            switch (dir) {
                case 0: TopQuads++; break;
                case 1: BottomQuads++; break;
                case 2:
                case 3: XQuads++; break;
                default: ZQuads++; break;
            }
        }

        /// <summary>一个四边形 12 个索引：双面（与列顶网格同口径：从任意一侧看都不能穿）。</summary>
        void PushQuadIndices(int v) {
            PushIndex(v);
            PushIndex(v + 1);
            PushIndex(v + 2);
            PushIndex(v);
            PushIndex(v + 2);
            PushIndex(v + 3);
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
                Array.Resize(ref m_attrData, m_vertexData.Length);
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
            VertexBuffer = new VertexBuffer(
                HasAttributes ? SkylineLodVertex.VertexDeclaration : TerrainVertex.VertexDeclaration, Vertices);
            if (HasAttributes) {
                VertexBuffer.SetData(m_attrData, 0, Vertices);
            }
            else {
                VertexBuffer.SetData(m_vertexData, 0, Vertices);
            }
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
            JsonObject result = new() {
                ["cube"] = new JsonArray(Cx, Cy, Cz),
                ["vertexFormat"] = HasAttributes ? "SkylineLodVertex(28B, normal+materialId)" : "TerrainVertex(20B)",
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
                ["indexCount"] = IndexCount,
                ["vertexBytes"] = VertexBytes,
                ["buildMs"] = Math.Round(BuildMs, 2)
            };
            if (HasAttributes) {
                result["distinctNormals"] = DistinctNormals;
                result["distinctMaterials"] = DistinctMaterials;
            }
            return result;
        }
    }

    /// <summary>桥：`skyline.SurfaceVoxelMesh(cx,cy,cz)`。</summary>
    public static partial class SkylineRuntime {
        /// <summary>
        /// 抓表面体素壳 → 建**两种**网格（烘焙格式 + 属性格式），报体素/面/四边形/字节与属性统计。
        /// 两种网格的几何逐位相同（这就是 `skyline.LodVolumeCompare()` 能对拍的前提）。
        /// </summary>
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
                SurfaceVoxelMesh baked = SurfaceVoxelMesh.Build(shell, true, true, false);
                SurfaceVoxelMesh attr = SurfaceVoxelMesh.Build(shell, true, true, true);
                CubeSurface32 heightShell = CubeSurface32.Extract(terrain, cx, cy, cz);
                CubeSurfaceMesh32 heightMesh = CubeSurfaceMesh32.Build([heightShell], 1, 1, true, true, 1);
                result["ok"] = true;
                result["voxelShell"] = shell.Describe();
                result["bakedMesh"] = baked.DescribeObject();
                result["attributeMesh"] = attr.DescribeObject();
                result["heightFieldMesh"] = heightMesh.DescribeObject();
                result["faceShading"] = SkylineFaceShading.Describe();
                // 两套网格的几何必须逐位相同（否则对拍无意义）：直接断言四边形/顶点/索引数
                result["geometryIdentical"] = baked.Quads == attr.Quads && baked.Vertices == attr.Vertices
                    && baked.IndexCount == attr.IndexCount;
                result["note"] = "bakedMesh = TerrainVertex + CPU 面因子（游戏 Opaque 着色器）；"
                    + "attributeMesh = SkylineLodVertex（法线 + 材质 id，SkylineLodVolume 消费）；"
                    + "heightFieldMesh = 列顶高度场（对照，表达不了体积）";
                baked.Dispose();          // 取证调用不留 GPU 资源
                attr.Dispose();
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
