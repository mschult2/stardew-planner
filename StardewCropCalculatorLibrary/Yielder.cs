using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace StardewCropCalculatorLibrary
{
    /// <summary>
    /// Yields if enough time has elapsed. Use in long-running loops to create a performant yield rate.
    /// </summary>
    internal class Yielder
    {
        private static readonly int FrameRate = 60;
        private static readonly double TickInMs = 1000.0 / Stopwatch.Frequency;
        // Memory threshold in GB. Necessary because browser tabs only allow WebAssembly apps to use 2 GB of memory. (Javascript is 4 GB)
        private static readonly double MemoryThreshold = 1.25;

        private readonly int yieldIntervalMs = 1000 / FrameRate;
        private long lastYieldTicks = Stopwatch.GetTimestamp();

        private readonly int memIntervalMs = 2000;
        private long lastMemTicks = Stopwatch.GetTimestamp();

        public int OperationCount { get; set; }
        public int CacheHitCount { get; set; }

        public Yielder() { }

        public Yielder(int yieldIntervalMs)
        {
            this.yieldIntervalMs = yieldIntervalMs;
        }

        /// <summary>
        /// Yield if enough time has elapsed.
        /// This slows down the yield rate to an acceptable performance level.
        /// </summary>
        internal async Task Yield()
        {
            bool IsTimeToYield = (Stopwatch.GetTimestamp() - lastYieldTicks) * TickInMs >= yieldIntervalMs;

            if (IsTimeToYield)
            {
                lastYieldTicks = Stopwatch.GetTimestamp();
                await Task.Delay(1);
            }
        }

        /// <summary> Returns true if memory limit is exceeded. </summary>
        internal bool CheckMemoryLimit()
        {
            bool IsTimeToCheck = (Stopwatch.GetTimestamp() - lastMemTicks) * TickInMs >= memIntervalMs;

            if (IsTimeToCheck)
            {
                lastMemTicks = Stopwatch.GetTimestamp();

                long memoryInBytes = GC.GetTotalMemory(false);
                double memoryInGB = memoryInBytes / (1024.0 * 1024.0 * 1024.0);

                Console.WriteLine($"Operations: {OperationCount}, cache hits: {CacheHitCount}");
                Console.WriteLine($"Memory usage: {memoryInGB:F3} GB");

                if (memoryInGB >= MemoryThreshold)
                    return true;
            }

            return false;
        }
    }
}
