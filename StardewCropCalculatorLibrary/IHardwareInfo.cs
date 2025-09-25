using System.Threading.Tasks;

namespace StardewCropCalculatorLibrary
{
    /// <summary>
    /// Information about system hardware.
    /// </summary>
    public interface IHardwareInfo
    {
        /// <summary>
        /// Get the maximum number of threads this hardware supports.
        /// </summary>
        ValueTask<int> GetThreadCapacity();
    }
}