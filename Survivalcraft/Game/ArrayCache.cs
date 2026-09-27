using Engine;

namespace Game {
    public class ArrayCache<T> {
        public ArrayCache(IEnumerable<int> bucketSizes, float minCacheRatio1, float minCacheTime1, float minCacheRatio2, float minCacheTime2) {
            m_buckets = bucketSizes.OrderBy(s => s).Select(s => new Bucket { Capacity = s }).ToArray();
            m_minCacheRatio1 = minCacheRatio1;
            m_minCacheDuration1 = minCacheTime1;
            m_minCacheRatio2 = minCacheRatio2;
            m_minCacheDuration2 = minCacheTime2;
            m_minCacheRatioLastTime1 = Time.FrameStartTime;
            m_minCacheRatioLastTime2 = Time.FrameStartTime;
            Window.LowMemory += ClearCache;
            Time.QueueTimeDelayedExecution(0.0, CheckCache);
        }

        public T[] Rent(int capacity, bool clearArray) {
            object @lock = m_lock;
            T[] array2;
            lock (@lock) {
                Bucket bucket = GetBucket(capacity);
                if (bucket != null) {
                    if (bucket.Stack.Count > 0) {
                        T[] array = bucket.Stack.Pop();
                        if (clearArray) {
                            Array.Clear(array, 0, array.Length);
                        }
                        m_cachedCount -= array.Length;
                        m_usedCount += array.Length;
                        array2 = array;
                    }
                    else {
                        m_usedCount += bucket.Capacity;
                        array2 = new T[bucket.Capacity];
                    }
                }
                else {
                    array2 = new T[capacity];
                }
            }
            return array2;
        }

        public void Return(T[] array) {
            object @lock = m_lock;
            lock (@lock) {
                Bucket bucket = GetBucket(array.Length);
                if (bucket != null) {
                    bucket.Stack.Push(array);
                    m_cachedCount += array.Length;
                    m_usedCount -= array.Length;
                }
                float num = CalculateCacheRatio();
                if (num >= m_minCacheRatio1) {
                    m_minCacheRatioLastTime1 = Time.FrameStartTime;
                }
                if (num >= m_minCacheRatio2) {
                    m_minCacheRatioLastTime2 = Time.FrameStartTime;
                }
            }
        }

        void CheckCache() {
            object @lock = m_lock;
            lock (@lock) {
                float num = CalculateCacheRatio();
                if ((num < m_minCacheRatio1 && Time.FrameStartTime - m_minCacheRatioLastTime1 > m_minCacheDuration1)
                    || (num < m_minCacheRatio2 && Time.FrameStartTime - m_minCacheRatioLastTime2 > m_minCacheDuration2)) {
                    ClearCache();
                }
                Time.QueueTimeDelayedExecution(Time.FrameStartTime + MathUtils.Min(m_minCacheDuration1, m_minCacheDuration2) / 5f, CheckCache);
            }
        }

        Bucket GetBucket(int capacity) {
            for (int i = 0; i < m_buckets.Length; i++) {
                if (m_buckets[i].Capacity >= capacity) {
                    return m_buckets[i];
                }
            }
            return null;
        }

        void ClearCache() {
            Bucket[] buckets = m_buckets;
            for (int i = 0; i < buckets.Length; i++) {
                buckets[i].Stack.Clear();
            }
            m_cachedCount = 0L;
            m_minCacheRatioLastTime1 = Time.FrameStartTime;
            m_minCacheRatioLastTime2 = Time.FrameStartTime;
        }

        float CalculateCacheRatio() {
            if (m_cachedCount <= 0L) {
                return 1f;
            }
            return m_usedCount / (float)(m_usedCount + m_cachedCount);
        }

        /// <summary>[v0.1.73] **诊断只读**：池里缓存了多少元素、外面租用了多少元素。
        /// 为什么要暴露：`ClearCache` 的触发条件是"缓存占比**低于**阈值"，也就是**几乎不会清**；
        /// 只靠 GC 堆总量无法区分"池里囤着"与"真的泄漏"（见 `notes/152`）。</summary>
        public long CachedCount => m_cachedCount;

        public long UsedCount => m_usedCount;

        /// <summary>[v0.1.73] 清空缓存池（诊断/内存压力时手动调用）。</summary>
        public void Clear() => ClearCache();

        object m_lock = new();

        Bucket[] m_buckets;

        long m_cachedCount;

        long m_usedCount;

        float m_minCacheRatio1;

        float m_minCacheDuration1;

        double m_minCacheRatioLastTime1;

        float m_minCacheRatio2;

        float m_minCacheDuration2;

        double m_minCacheRatioLastTime2;

        class Bucket {
            public int Capacity;

            public Stack<T[]> Stack = new();
        }
    }
}
