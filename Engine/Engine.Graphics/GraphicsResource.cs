namespace Engine.Graphics {
    public abstract class GraphicsResource : IDisposable {
        public static HashSet<GraphicsResource> m_resources = [];

        public bool m_isDisposed;

        /// <summary>
        /// [v0.1.97 · 诊断] **抓"下一个被创建的 GPU 资源"的创建栈**（默认关，只有显式 arm 才记录）。
        ///
        /// 为什么需要：`m_resources` 是个**静态** HashSet，没 `Dispose()` 的资源**永远可达**
        /// （终结器也轮不到）⇒ 实测站着不动也**每帧漏 1 个 VB + 1 个 IB**（约 0.25 MiB/s），
        /// 而所有 Skyline 子系统关掉都照样漏。要定位就只能**点名到调用栈**。
        /// </summary>
        public static bool m_captureNext;
        public static string m_captureTypeName;
        public static string m_captureStackTrace;

        public GraphicsResource() {
            m_resources.Add(this);
            if (m_captureNext) {
                m_captureNext = false;
                m_captureTypeName = GetType().FullName;
                m_captureStackTrace = Environment.StackTrace;
            }
        }

        ~GraphicsResource() {
            Dispatcher.Dispatch(delegate { Dispose(); });
        }

        public virtual void Dispose() {
            m_isDisposed = true;
            m_resources.Remove(this);
        }

        public abstract int GetGpuMemoryUsage();

        public abstract void HandleDeviceLost();

        public abstract void HandleDeviceReset();

        public void VerifyNotDisposed() {
            if (m_isDisposed) {
                throw new InvalidOperationException("GraphicsResource is disposed.");
            }
        }
    }
}
