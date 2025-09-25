using Microsoft.JSInterop;
using StardewCropCalculatorLibrary;

namespace CropPlanner
{
    /// <summary>
    /// Information about system hardware.
    /// </summary>
    public sealed class HardwareInfo(IJSRuntime js) : IHardwareInfo, IAsyncDisposable
    {
        // If logical processor count is not available, then use a safe lower-bound for mobile devices/computers.
        private static readonly int DefaultThreadCapacity = 4;
        private IJSObjectReference? module;

        /// <summary>
        /// Get the maximum number of threads this hardware supports.
        /// </summary>
        public async ValueTask<int> GetThreadCapacity()
        {
            module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/hardware.js");

            int logicalProcessorCount = 0;

            if (module is not null)
            {
                logicalProcessorCount = await module.InvokeAsync<int>("getHardwareConcurrency");

                if (logicalProcessorCount > 0)
                {
                    return logicalProcessorCount;
                }
                else
                {
                    Console.WriteLine($"[HardwareInfo] WARNING: logical processor count is invalid. Using default thread capacity: {DefaultThreadCapacity}");
                    return DefaultThreadCapacity;
                }
            }
            else
            {
                Console.WriteLine($"[HardwareInfo] WARNING: logical processor count is unavailable. Using default thread capacity: {DefaultThreadCapacity}");
                return DefaultThreadCapacity;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (module is not null)
                try { await module.DisposeAsync(); } catch { Console.WriteLine($"[HardwareInfo] ERROR: failed to dispose HardwareInfo"); }
        }
    }
}
