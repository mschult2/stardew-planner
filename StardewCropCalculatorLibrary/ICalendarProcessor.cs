using System.Collections.Generic;
using System.Threading.Tasks;
using static StardewCropCalculatorLibrary.GameStateCalendarFactory;

namespace StardewCropCalculatorLibrary
{
    public interface ICalendarProcessor
    {
        Task InitializeAsync();

        Task ConfigureAsync(double startingGold, int startingTiles, int numDays, double cheapestCropBuyPrice, IEnumerable<Crop> crops);

        /// <summary>
        /// Process an entire level of nodes, returning the children. Breadth.
        /// </summary>
        Task<IEnumerable<(int InputIndex, GetMostProfitableCropArgs Node)>> ProcessAsync_Shallow(IEnumerable<(int Day, string SerializedCalendar)> inputNodes);

        /// <summary>
        /// For each node, process it and all of its children nodes, returning the best schedule. Depth.
        /// A best schedule is returned for each input node, unless that input was already best. In which case that schedule is exempt from the list.
        /// </summary>
        //Task<IEnumerable<(int InputIndex, GetMostProfitableCropArgs Node)>> ProcessAsync_DeepFull(IEnumerable<(int Day, string SerializedCalendar)> inputNodes);
        Task<IEnumerable<(int InputIndex, GetMostProfitableCropArgs Node)>> ProcessAsync_Deep(IEnumerable<GetMostProfitableCropArgs> inputNodes);

        /// <summary> Stats: the number of nodes processed in the last call. </summary>
        int LastNodesProcessed { get; }

        /// <summary> Stats: the number of processed nodes that were cache hits, bypassing processing, in the last call. </summary>
        int LastCacheHitsProcessed { get; }

        /// <summary> Enables stats. </summary>
        bool EnableStats { get; }

        /// <summary> Print stats about workers. </summary>
        void PrintStats();
    }
}
