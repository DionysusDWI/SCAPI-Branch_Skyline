using Silk.NET.OpenGLES;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Engine.Graphics {
    public class IndexBuffer : GraphicsResource {
        public int m_buffer;

        public string DebugName {
            get {
                return string.Empty;
            }
            // ReSharper disable ValueParameterNotUsed
            set
            // ReSharper restore ValueParameterNotUsed
            {
                // For Direct3D backend
            }
        }

        public IndexFormat IndexFormat { get; set; }

        public int IndicesCount { get; set; }

        public object Tag { get; set; }

        /// <summary>
        /// 强制所有索引缓冲使用 32 位格式（旧行为）。
        /// 用于兼容依赖 32 位索引的模组，或排查 16 位索引相关问题时的一键回退。
        /// </summary>
        public static bool ForceThirtyTwoBits { get; set; }

        /// <summary>
        /// 按顶点池大小选择索引格式：顶点数不超过 65535 用 16 位（索引带宽减半），否则 32 位。
        /// </summary>
        public static IndexFormat FormatForVertexCount(int vertexCount) {
            return ForceThirtyTwoBits || vertexCount > 65535
                ? IndexFormat.ThirtyTwoBits
                : IndexFormat.SixteenBits;
        }

        public IndexBuffer(IndexFormat indexFormat, int indicesCount) {
            InitializeIndexBuffer(indexFormat, indicesCount);
            AllocateBuffer();
        }

        public override void Dispose() {
            base.Dispose();
            DeleteBuffer();
        }

        public void SetData<T>(T[] source, int sourceStartIndex, int sourceCount, int targetStartIndex = 0) where T : unmanaged {
            VerifyParametersSetData(source, sourceStartIndex, sourceCount, targetStartIndex);
            int num = Utilities.SizeOf<T>();
            int size = IndexFormat.GetSize();
            if (num == size || (num == 1 && size == 4)) {
                // 元素宽度与索引宽度一致（或旧式 byte 流写入 32 位缓冲）：直接上传
                UploadDirect(source, sourceStartIndex, sourceCount, targetStartIndex);
                return;
            }
            if ((typeof(T) == typeof(int) || typeof(T) == typeof(uint)) && size == 2) {
                // 有符号/无符号 32 位索引写入 16 位缓冲：收窄并检查溢出，全部校验通过才写 GPU，失败时缓冲内容不受影响
                if (targetStartIndex + sourceCount > IndicesCount) {
                    throw new ArgumentException("Range is out of target bounds.");
                }
                var converted = new ushort[sourceCount];
                for (int i = 0; i < sourceCount; i++) {
                    long value = typeof(T) == typeof(int)
                        ? Unsafe.As<T, int>(ref source[sourceStartIndex + i])
                        : Unsafe.As<T, uint>(ref source[sourceStartIndex + i]);
                    if (value < 0
                        || value > 65535) {
                        throw new OverflowException(
                            $"Index value {value} at position {sourceStartIndex + i} does not fit in a {IndexFormat} index buffer."
                        );
                    }
                    converted[i] = (ushort)value;
                }
                UploadDirect(converted, 0, sourceCount, targetStartIndex);
                return;
            }
            if (typeof(T) == typeof(ushort) && size == 4) {
                // 16 位索引写入 32 位缓冲：加宽
                if (targetStartIndex + sourceCount > IndicesCount) {
                    throw new ArgumentException("Range is out of target bounds.");
                }
                var converted = new int[sourceCount];
                for (int i = 0; i < sourceCount; i++) {
                    converted[i] = Unsafe.As<T, ushort>(ref source[sourceStartIndex + i]);
                }
                UploadDirect(converted, 0, sourceCount, targetStartIndex);
                return;
            }
            throw new InvalidOperationException(
                $"Cannot upload an array of {typeof(T).Name} into a {IndexFormat} index buffer."
            );
        }

        private void UploadDirect<T>(T[] source, int sourceStartIndex, int sourceCount, int targetStartIndex) where T : unmanaged {
            GCHandle gCHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
            try {
                int num = Utilities.SizeOf<T>();
                int size = IndexFormat.GetSize();
                unsafe {
                    GLWrapper.BindBuffer(BufferTargetARB.ElementArrayBuffer, m_buffer);
                    GLWrapper.GL.BufferSubData(
                        BufferTargetARB.ElementArrayBuffer,
                        new IntPtr(targetStartIndex * size),
                        new UIntPtr((uint)(num * sourceCount)),
                        (gCHandle.AddrOfPinnedObject() + sourceStartIndex * num).ToPointer()
                    );
                }
            }
            finally {
                gCHandle.Free();
            }
        }

        public override void HandleDeviceLost() {
            DeleteBuffer();
        }

        public override void HandleDeviceReset() {
            AllocateBuffer();
        }

        public void AllocateBuffer() {
            unsafe {
                GLWrapper.GL.GenBuffers(1, out uint buffer);
                m_buffer = (int)buffer;
                GLWrapper.BindBuffer(BufferTargetARB.ElementArrayBuffer, m_buffer);
                GLWrapper.GL.BufferData(
                    BufferTargetARB.ElementArrayBuffer,
                    new UIntPtr((uint)(IndexFormat.GetSize() * IndicesCount)),
                    null,
                    BufferUsageARB.StaticDraw
                );
            }
        }

        public void DeleteBuffer() {
            if (m_buffer != 0) {
                GLWrapper.DeleteBuffer(BufferTargetARB.ElementArrayBuffer, m_buffer);
                m_buffer = 0;
            }
        }

        public override int GetGpuMemoryUsage() => IndicesCount * IndexFormat.GetSize();

        void InitializeIndexBuffer(IndexFormat indexFormat, int indicesCount) {
            if (indicesCount <= 0) {
                throw new ArgumentException("Indices count must be greater than 0.");
            }
            IndexFormat = indexFormat;
            IndicesCount = indicesCount;
        }

        void VerifyParametersSetData<T>(T[] source, int sourceStartIndex, int sourceCount, int targetStartIndex = 0) where T : unmanaged {
            VerifyNotDisposed();
            int num = Utilities.SizeOf<T>();
            int size = IndexFormat.GetSize();
            ArgumentNullException.ThrowIfNull(source);
            if (sourceStartIndex < 0
                || sourceCount < 0
                || sourceStartIndex + sourceCount > source.Length) {
                throw new ArgumentException("Range is out of source bounds.");
            }
            if (targetStartIndex < 0
                || targetStartIndex * size + sourceCount * num > IndicesCount * size) {
                throw new ArgumentException("Range is out of target bounds.");
            }
        }
    }
}