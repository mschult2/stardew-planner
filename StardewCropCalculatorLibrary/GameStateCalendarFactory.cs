using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StardewCropCalculatorLibrary
{
    public static class QueueExtensions
    {
        public static void EnqueueRange<T>(this Queue<T> queue, IEnumerable<T> items)
        {
            foreach (var item in items)
            {
                queue.Enqueue(item);
            }
        }
    }

    /// <summary> The state of our farm on every day. Includes the day after the last day of the season, since we still get paid on that day. </summary>
    public class GameStateCalendar
    {
        public readonly int NumDays;

        public double Wealth => GameStates[NumDays + 1].Wallet;

        /// <summary> The state of our farm on a particular day. Ie, how many plants, free tiles, and gold we have. </summary>
        public readonly SortedDictionary<int, GameState> GameStates = new SortedDictionary<int, GameState>();

        /// <summary>
        /// Get all planted crops in the order we plant them.
        /// </summary>
        /// <returns>Non-null list. Empty if nothing planted.</returns>
        public List<PlantBatch> GetPlantSequence()
        {
            var plantBatchSequence = new List<PlantBatch>();
            var plantBatchIdSequence = new List<string>();

            foreach (KeyValuePair<int, GameState> curGameStatePair in GameStates)
            {
                var curDay = curGameStatePair.Key;
                var curGameState = curGameStatePair.Value;

                if (curGameState.DayOfInterest)
                {
                    foreach (var plantBatch in curGameState.Plants)
                    {
                        if (!plantBatchIdSequence.Contains(plantBatch.Id))
                        {
                            plantBatchSequence.Add(plantBatch);
                            plantBatchIdSequence.Add(plantBatch.Id);
                        }
                    }
                }
            }

            return plantBatchSequence;
        }

        private GameStateCalendar(int numDays)
        {
            NumDays = numDays;
        }

        public GameStateCalendar(int numDays, int availableTiles, double availableGold)
        {
            NumDays = numDays;

            // Adding one more day in case a crop is harvested on the last day.
            // In this case, we don't get our payday until the following day. So technically
            // that following day may have a state we care about, ie a larger balance.
            for (int i = 1; i <= numDays + 1; ++i)
            {
                GameStates.Add(i, new GameState());
                GameStates[i].Wallet = availableGold;
                GameStates[i].FreeTiles = availableTiles;
            }
        }

        /// <summary>
        /// Deserialize GameStateCalendar.
        /// Days before the first listed are omitted for perf reasons.
        /// Plants are omitted for perf reasions.
        /// </summary>
        public GameStateCalendar(int numDays, Dictionary<string, Crop> cropDictonary, string serializedCalendar)
        {
            NumDays = numDays;

            string[] lines = serializedCalendar.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var daysOfInterest = new Dictionary<int, GameState>();
            int firstDay = 1;
            bool haveFirstDay = false;

            foreach (var line in lines)
            {
                // Each line is: day_wallet_freeTiles
                var parts = line.Split('_');

                int day = int.Parse(parts[0]);
                double wallet = double.Parse(parts[1]);
                int freeTiles = int.Parse(parts[2]);

                if (!haveFirstDay)
                {
                    haveFirstDay = true;
                    firstDay = day;
                }

                // Create GameState
                daysOfInterest[day] = new GameState() { Wallet = wallet, FreeTiles = freeTiles, DayOfInterest = true };

                // Append plant list to GameState
                string serializedPlantBatches = parts[3];
                if (!string.IsNullOrWhiteSpace(serializedPlantBatches))
                {
                    var plantBatchesParts = serializedPlantBatches.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var serializedPlantBatch in plantBatchesParts)
                    {
                        var plantBatchParts = serializedPlantBatch.Split(';');

                        string cropName = plantBatchParts[0];
                        int cropCount = int.Parse(plantBatchParts[1]);
                        int plantDay = int.Parse(plantBatchParts[2]);

                        daysOfInterest[day].Plants.Add(new PlantBatch(cropDictonary[cropName], cropCount, plantDay, numDays));
                    }
                }
            }

            double lastWallet = 0;
            int lastTiles = 0;
            var lastPlants = new List<PlantBatch>();

            for (int i = firstDay; i <= numDays + 1; ++i)
            {
                if (daysOfInterest.TryGetValue(i, out var dayOfInterest))
                {
                    GameStates.Add(i, dayOfInterest);
                    lastWallet = dayOfInterest.Wallet;
                    lastTiles = dayOfInterest.FreeTiles;
                    lastPlants = dayOfInterest.Plants;
                }
                else
                {
                    GameStates.Add(i, new GameState());
                    GameStates[i].Wallet = lastWallet;
                    GameStates[i].FreeTiles = lastTiles;

                    foreach (var curLastPlant in lastPlants)
                        GameStates[i].Plants.Add(curLastPlant);
                }
            }
        }

        /// <summary>
        /// Clone input calendar. Input range is deep copy that is safe to modify. The other days are omitted.
        /// </summary>
        /// <param name="otherCalendar">Input calendar to copy.</param>
        /// <param name="startingDay">Use 1 to start at beginning of season.</param>
        /// <param name="endingDay">Use 0 to go to end of season.</param>
        public GameStateCalendar(GameStateCalendar otherCalendar, int startingDay, int endingDay, bool deep = true)
        {
            NumDays = otherCalendar.NumDays;

            // Deep copy of indicated range
            if (deep)
            {
                for (int i = startingDay; i <= endingDay; ++i)
                    GameStates.Add(i, new GameState());
            }

            Merge(otherCalendar, startingDay, endingDay, deep);

            //// Shallow copy of other range
            //for (int i = 1; i <= NumDays + 1; ++i)
            //{
            //    if (i < startingDay || i > endingDay)
            //        GameStates.Add(i, otherCalendar.GameStates[i]);
            //}
        }

        /// <summary>
        /// DeepCopy otherCalendar onto this calendar, within a certain range.
        /// Values outside the indicated range are left untouched.
        /// </summary>
        /// <param name="otherCalendar">The calendar to copy</param>
        /// <param name="startingDay">The day to copy from. Default is 1.</param>
        /// <param name="endingDay">The day to end copying on. Default is day after the last day of the season.</param>
        public void Merge(GameStateCalendar otherCalendar, int startingDay = 1, int endingDay = 0, bool deep = true)
        {
            if (endingDay == 0)
                endingDay = NumDays + 1;

            for (int i = startingDay; i <= endingDay; ++i)
            {
                if (deep)
                {
                    GameStates[i].Wallet = otherCalendar.GameStates[i].Wallet;
                    GameStates[i].FreeTiles = otherCalendar.GameStates[i].FreeTiles;
                    GameStates[i].DayOfInterest = otherCalendar.GameStates[i].DayOfInterest;

                    GameStates[i].Plants.Clear();

                    foreach (PlantBatch otherPlantBatch in otherCalendar.GameStates[i].Plants)
                        GameStates[i].Plants.Add(new PlantBatch(otherPlantBatch));
                }
                else
                {
                    GameStates[i] = otherCalendar.GameStates[i];
                }
            }
        }

        /// <summary>
        /// Return a new schedule shifted forward by a few days (shallow copy). Example: instead of starting at day 1, it starts at day 15.
        /// </summary>
        /// <param name="calendar">The calendar to use a shifted version of.</param>
        /// <param name="daysToShift">Number of days to shift the schedule forward.</param>
        static public GameStateCalendar Shift(GameStateCalendar calendar, int daysToShift)
        {
            GameStateCalendar newCalendar = new GameStateCalendar(calendar.NumDays);

            foreach (var gameStatePair in calendar.GameStates)
            {
                var oldState = gameStatePair.Value;

                var newState = newCalendar.GameStates[gameStatePair.Key + daysToShift] = oldState;

                for (int i = 0; i < oldState.Plants.Count; ++i)
                {
                    PlantBatch oldBatch = newState.Plants[i];
                    newState.Plants[i] = new PlantBatch(oldBatch.CropType, oldBatch.Count, oldBatch.PlantDay + daysToShift, oldBatch.NumDays + daysToShift);
                }
            }

            return newCalendar;
        }

        public override string ToString()
        {
            StringBuilder sb = new();

            foreach (var gameStatePair in GameStates)
            {
                var day = gameStatePair.Key;
                var gameState = gameStatePair.Value;

                sb.Append($"Day {day}: {gameState.Plants.Count} plants, {gameState.Wallet}g  ");

                sb.Append('(');
                for (int plantIndex = 0; plantIndex < gameState.Plants.Count; ++plantIndex)
                {
                    if (plantIndex > 0)
                        sb.Append('-');

                    var plant = gameState.Plants[plantIndex];
                    sb.Append($"{plant.Count} {plant.CropType}; {plant.PlantDay}");
                }
                sb.AppendLine(")");
            }

            return sb.ToString();
        }
    }

    /// <summary> The state of our farm on a given day. </summary>
    public class GameState
    {
        /// <summary>  How much gold we have. </summary>
        public double Wallet = 0;

        /// <summary>  How many free tiles we have. </summary>
        public int FreeTiles = 0;

        /// <summary> Crops currently planted on the farm. Crops are grouped by batch. </summary>
        public readonly List<PlantBatch> Plants = new List<PlantBatch>();

        /// <summary>  Something happens on this day - either we get more gold, or more tiles. </summary>
        public bool DayOfInterest = false;

        public override string ToString()
        {
            string plantsDescription = "";
            foreach (var batch in Plants)
                plantsDescription += $"{batch.CropType.name}: {batch.Count}, ";

            return $"{plantsDescription}available tiles: {FreeTiles}, available gold: {Wallet}";
        }
    }

    /// <summary>
    /// A batch of crops planted on our farm. All one type, all planted at the same time.
    /// A batch can be treated as one super-plant harvested all at once.
    /// This class meant to be IMMUTABLE. Do not modify after construction.
    /// </summary>
    public class PlantBatch
    {
        public Crop CropType { get; }
        public int Count { get; }
        /// <summary> PlantBatch is supposed to be read-only so it can be reused, so don't modify this after construction. </summary>
        public SortedSet<int> HarvestDays { get; } = new SortedSet<int>();
        public bool Persistent => CropType.IsPersistent(NumDays);
        public int NumDays { get; }

        public string Id { get; }

        public int PlantDay { get; }

        public PlantBatch(Crop cropType, int cropCount, int plantDay, int numDays)
        {
            Id = Guid.NewGuid().ToString();

            NumDays = numDays;
            CropType = cropType;
            Count = cropCount;
            PlantDay = plantDay;

            foreach (var harvestDay in cropType.HarvestDays(plantDay, numDays))
                HarvestDays.Add(harvestDay);
        }

        public PlantBatch(PlantBatch otherPlantBatch)
        {
            Id = otherPlantBatch.Id;
            CropType = otherPlantBatch.CropType;
            Count = otherPlantBatch.Count;
            PlantDay = otherPlantBatch.PlantDay;

            foreach (int otherHarvestDay in otherPlantBatch.HarvestDays)
                HarvestDays.Add(otherHarvestDay);
        }

        public override string ToString()
        {
            return $"{Count} {CropType.name}";
        }
    }

    /// <summary>
    /// This scheduler algorithm is an approximate gamestate simulation. Thus it runs slow.
    /// The benefit is it can deal with limited tiles, or any input.
    /// </summary>
    public class GameStateCalendarFactory
    {
        public class GetMostProfitableCropArgs
        {
            public int Day;
            public GameStateCalendar Calendar;

            public GetMostProfitableCropArgs(int day, GameStateCalendar calendar)
            {
                Day = day;
                Calendar = calendar;
            }
        }

        /// <summary> Algorithm used to process simulation tree (in strategy 1). </summary>
        private enum ProcessorAlgorithm
        {
            /// <summary> Normal: process each node sequentially. </summary>
            Sequential = 0,
            /// <summary> Process nodes in parallel to receive the next level of children nodes. </summary>
            ParallelShallow = 1,
            /// <summary> Process nodes in parallel, the entire subtree, to receive the corresponding best leaf node. </summary>
            ParallelDeep = 2,
        }

        // Gold pruning: if gold is quite low we ignore it, to simplify the schedule. Expressed as fraction of starting gold. 0 means we process any amount of gold, and 1 means we ignore an amount equal to the starting gold.
        private static readonly double GoldInvestmentThreshold = 0.5;
        private static readonly double TileInvestmentThresold = 0.07;
        // Used to bucket cache results.
        private static readonly int SignificantDigits = 2;
        // Optimization: cache computed nodes.
         private static readonly bool UseCache = true;
        // Optimization: max number of crops to include in schedule. Currently, 4 is the limit with 12 crops because there's 20k/2k-opt ops at 7 seconds. 5 would be 250k/100k-opt ops at 6 minutes!
        private static readonly int MaxNumCropTypes = 5;
        // (Only matters for next-day Payday) If false, then return tiles as soon as crop is harvested, which is more realistic. And it helps with cases where we needed just *one more day* to make a sale.
        //    -> EXAMPLE: MikeFruit: 14, NA, 200, 400; Gold: 40,000; Tiles: 100; SeasonLength: 29, DayAfter. Profit is 40k instead of 20k, since we were only tile-limited and so had time to plant 1 more batch. 
        // However, holding onto those tiles until we get the next-day gold might be smarter in some cases...because what if we're in a very precise corner case where we are tile-limited, but only have enough
        // surplus gold to buy a cheaper crop (like Hot Pepper), when the next day we would've had enough money to plant a Starfruit.
        // Really the best solve seems to add a no-crop option, so we can always delay planting. But I think that would wreck performance, because now every choice has a minimum span of 1 day.
        //    -> COUNTEREXAMPLE: MikeFruit + CheapFruit: 10, NA, 50, 150; Gold: 300; Tiles: 1; SeasonLength: 30, DayAfter. Profit is 300 instead of 400, because we chose to plant CheapFruit instead of waiting a day and planting StarFruit.
        //       This situation occurs because we were tile-limited, but the gold-limit was so close behind we couldn't afford the better fruit.
        // Summary: Answer is also a little worse for test 10 Coral Island when using DayAfter. For that reason I won't enable it. I mean the user has to enable DayAfter, so really we should respect their choice.
        private static readonly bool ReturnTilesAsap = false;
        // Allow the planting of multiple crops on the same day.
        private static readonly bool MultiCrop = true;
        // Delegate computation to external processor.
        private static readonly ProcessorAlgorithm Algorithm = ProcessorAlgorithm.ParallelDeep;

        // When using the Deep processor, this is the number of nodes created by the Direct processor first.
        private static readonly int DeepProcessor_NumSeeds = 120;

        private readonly Yielder yielder = new();
        private readonly ICalendarProcessor processor;

        // GameState cache
        //private readonly Dictionary<string, (double Wealth, GameStateCalendar Calendar)> answerCache = [];
        private readonly HashSet<string> nodeCache = [];

        private int NumDays;
        private int StartDay;
        private List<Crop> Crops = [];

        private Queue<GetMostProfitableCropArgs> daysToEvaluate = new Queue<GetMostProfitableCropArgs>(3000);

        private int startingTiles = 0;
        private double startingGold = 0;

        private Crop cheapestCrop = null;

        /// <summary>
        /// The time between the day on which we harvest and the day on which we get gold.
        /// </summary>
        public static int PaydayDelay { get; set; } = 0;

        public GameStateCalendarFactory(ICalendarProcessor processor)
        {
            this.processor = processor;
            processor.InitializeAsync();
        }

        /// <summary>
        /// Return the optimial schedule.
        /// Only run one at a time!
        /// </summary>
        public async Task<Tuple<double, GameStateCalendar>> GetBestSchedule(List<Crop> crops, int startDay, int totalDays, int availableTiles, double availableGold)
        {
            // Validate inputs
            if (startDay < 1 || startDay >= totalDays)
                throw new Exception($"Starting day was invalid: {startDay}");

            // Initialize
            StartDay = startDay;
            NumDays = totalDays - startDay + 1;
            Crops = crops.Where(c => c.IsEnabled).ToList();

            if (Crops != null && Crops.Count > 0)
            {
                cheapestCrop = Crops[0];
                foreach (var crop in Crops)
                    cheapestCrop = crop.buyPrice < cheapestCrop.buyPrice ? crop : cheapestCrop;
            }

            nodeCache.Clear();
            daysToEvaluate.Clear();
            int numCropTypes = MaxNumCropTypes;

            bool infiniteGold = false;

            // If tiles are infinite but gold is still finite, then we can still calculate finite end wealth.
            if (availableTiles <= 0)
                availableTiles = -1;

            if (availableGold <= 0)
            {
                availableGold = 100000000;
                infiniteGold = true;
            }

            double tg = availableTiles == -1 ? 0 : availableTiles / availableGold;

            // Trigger perf break if high tile-to-gold ratio, since those take a long time to compute.
            if (tg > 0.4)
            {
                Console.WriteLine($"Passed TG limit: {tg}. Lowering crop limit to 2.");
                numCropTypes = Math.Min(numCropTypes, 2);
            }
            else if (tg > 0.2)
            {
                Console.WriteLine($"Passed TG limit: {tg}. Lowering crop limit to 3.");
                numCropTypes = Math.Min(numCropTypes, 3);
            }
            else if (tg > 0.1)
            {
                Console.WriteLine($"Passed TG limit: {tg}. Lowering crop limit to 4.");
                numCropTypes = Math.Min(numCropTypes, 4);
            }

            startingGold = availableGold;
            startingTiles = availableTiles;
            Tuple<double, GameStateCalendar> wealth = Tuple.Create<double, GameStateCalendar>(0.0, null);

            GameStateCalendar bestPpiCalendar = null;
            double bestPpiWealth = 0;
            string bestPpiIteration = null;

            // STRATEGY 1: PPI TopCrop
            var localCrops = new List<Crop>(Crops);

            for (int i = 0; i < Crops.Count; ++i)
            {
                if (localCrops.Count == 0)
                    break;

                GameStateCalendar ppiCalendar = new GameStateCalendar(NumDays, availableTiles, availableGold);

                GetBestSchedule_Strategy2(1, localCrops, ppiCalendar);
                double ppiWealth = ppiCalendar.GameStates[NumDays + 1].Wallet;

                // Save off best TopCrop scehdule
                var plantBatchSequence = ppiCalendar.GetPlantSequence();
                int uniqueCropsCount = plantBatchSequence.Select(x => x.CropType).Distinct().ToList().Count;
                if (ppiWealth > bestPpiWealth)
                {
                    bestPpiCalendar = ppiCalendar;
                    bestPpiWealth = ppiWealth;
                    bestPpiIteration = $"~TopCrop Iteration {i}~";
                }

                // Remove top crop
                var dayOnePlants = ppiCalendar.GameStates[1].Plants;
                if (dayOnePlants != null && dayOnePlants.Count > 0 && dayOnePlants[0] != null && dayOnePlants[0].Count > 0 && localCrops.Count > 0 && localCrops.Contains(dayOnePlants[0].CropType))
                    localCrops.Remove(dayOnePlants[0].CropType);
            }

            // STRATEGY 2 - PPI AllCrop
            localCrops = new List<Crop>(Crops);
            var bestCrops = new HashSet<Crop>();

            for (int i = 0; i < Crops.Count; ++i)
            {
                if (localCrops.Count == 0)
                    break;

                GameStateCalendar ppiCalendar = new GameStateCalendar(NumDays, availableTiles, availableGold);

                GetBestSchedule_Strategy2(1, localCrops, ppiCalendar);
                double ppiWealth = ppiCalendar.GameStates[NumDays + 1].Wallet;

                // Save off best AllCrop scehdule
                var plantBatchSequence = ppiCalendar.GetPlantSequence();
                int uniqueCropsCount = plantBatchSequence.Select(x => x.CropType).Distinct().ToList().Count;
                if (ppiWealth > bestPpiWealth)
                {
                    bestPpiCalendar = ppiCalendar;
                    bestPpiWealth = ppiWealth;
                    bestPpiIteration = $"~AllCrop Iteration {i}~";
                }

                if (plantBatchSequence.Count > 0)
                {
                    for (int plantIndex = 0; plantIndex < plantBatchSequence.Count; ++plantIndex)
                    {
                        var cropToAdd = plantBatchSequence[plantIndex].CropType;
                        localCrops.Remove(cropToAdd);

                        if (bestCrops.Count < numCropTypes)
                            bestCrops.Add(cropToAdd);
                    }
                }
                else
                    break;
            }

            if (bestCrops.Count < numCropTypes && bestCrops.Count < Crops.Count)
                Console.WriteLine($"WARNING: Best crop list wasn't as long as it's supposed to be. Length {bestCrops.Count} instead of {numCropTypes}.");

            // STRATEGY 3: Sim + PPI AllCrop heuristic
            {
                var calendar = new GameStateCalendar(NumDays, availableTiles, availableGold);

                daysToEvaluate.Enqueue(new GetMostProfitableCropArgs(1, calendar));

                wealth = await GetBestSchedule_Strategy1(bestCrops);
            }

            // Take whichever strategy is best
            if (bestPpiWealth > wealth.Item1)
            {
                Console.WriteLine($"Using PPI schedule {bestPpiIteration} since it's better than Sim.");
                wealth = Tuple.Create(bestPpiWealth, bestPpiCalendar);
            }

            // Modify answer to be profit instead of wealth
            if (infiniteGold)
            {
                double newProfit = wealth.Item1 - availableGold;
                wealth = Tuple.Create(newProfit, wealth.Item2);
            }

            // Modify answer to start on appropriate day
            if (StartDay > 1)
            {
                var shiftedCalendar = GameStateCalendar.Shift(wealth.Item2, StartDay - 1);
                wealth = Tuple.Create(wealth.Item1, shiftedCalendar);
            }

            yielder.CheckMemoryLimit();

            // Clean up
            nodeCache.Clear();
            daysToEvaluate.Clear();

            return wealth;
        }

        /// <summary>
        /// Return the maximum amount of gold you can end the season with, and the schedule that accompanies it.
        /// 
        /// Algorithm is a complete state space simulation with various optimizations.
        /// Very memory and CPU intensive.
        /// </summary>
        private async Task<Tuple<double, GameStateCalendar>> GetBestSchedule_Strategy1(IEnumerable<Crop> crops)
        {
            Console.WriteLine($"[GSCF] Using algorithm {Algorithm}");

            // Outputs
            double bestWealth = 0;
            GameStateCalendar bestCalendar = null;

            if (Algorithm > 0)
                await processor.ConfigureAsync(startingGold, startingTiles, NumDays, cheapestCrop.buyPrice, crops);

            ProcessorAlgorithm currentAlgorithm = Algorithm == ProcessorAlgorithm.ParallelDeep ? ProcessorAlgorithm.Sequential : Algorithm;

            // Stats
            Stopwatch debugStopwatch = Stopwatch.StartNew();
            yielder.OperationCount = 0;
            yielder.CacheHitCount = 0;

            while (daysToEvaluate.Count > 0)
            {
                // If using Subtree Method, first use Direct Method for a few iterations to get some good subtree options.
                if (currentAlgorithm != Algorithm)
                    currentAlgorithm = daysToEvaluate.Count >= DeepProcessor_NumSeeds ? ProcessorAlgorithm.ParallelDeep : ProcessorAlgorithm.Sequential;

                List<GetMostProfitableCropArgs> inputNodes;
                IEnumerable<(int InputIndex, GetMostProfitableCropArgs Node)> groupedOutputNodes;

                // Subtree Method: process one node all the way down.
                if (currentAlgorithm == ProcessorAlgorithm.ParallelDeep)
                {
                    inputNodes = [.. daysToEvaluate];
                    daysToEvaluate.Clear();

                    groupedOutputNodes = await processor.ProcessAsync_Deep(inputNodes);

                    // Exit if excessive memory usage.
                    if (groupedOutputNodes is null)
                        return MemoryFailure();

                    yielder.OperationCount += processor.LastNodesProcessed;
                    yielder.CacheHitCount += processor.LastCacheHitsProcessed;
                }
                // Frontier Method: process all nodes in level.
                else if (currentAlgorithm == ProcessorAlgorithm.ParallelShallow)
                {
                    inputNodes = [.. daysToEvaluate];
                    daysToEvaluate.Clear();

                    IEnumerable<(int Day, string SerCal)> serInputNodes = inputNodes.Select(node => (node.Day, SerializeGameStateCalendar(node.Calendar, node.Day)));

                    groupedOutputNodes = await processor.ProcessAsync_Shallow(serInputNodes);

                    // Exit if excessive memory usage.
                    if (groupedOutputNodes is null)
                        return MemoryFailure();

                    yielder.OperationCount += inputNodes.Count;
                    yielder.CacheHitCount += processor.LastCacheHitsProcessed;
                }
                // Direct Method: process one node.
                else
                {
                    // Exit if excessive memory usage.
                    if (yielder.CheckMemoryLimit())
                        return MemoryFailure();

                    // Yield back to renderer so page doesn't freeze.
                    await yielder.Yield();

                    var inputNode = daysToEvaluate.Dequeue();
                    inputNodes = [inputNode];
                    yielder.OperationCount += inputNodes.Count;

                    // Serialize input node.
                    (int Day, string SerCal) serInputNode = (inputNode.Day, SerializeGameStateCalendar(inputNode.Calendar, inputNode.Day));

                    // Check cache if node has been explored before.
                    if (UseCache && nodeCache.Contains(serInputNode.SerCal))
                    {
                        ++yielder.CacheHitCount;
                        continue;
                    }

                    // Process input node.
                    var outputNodes = ProcessNode(serInputNode.Day, serInputNode.SerCal, cheapestCrop.buyPrice, startingGold, startingTiles, crops, NumDays);
                    groupedOutputNodes = outputNodes.Select(node => (0, node));

                    // Mark output node in cache.
                    if (UseCache && serInputNode.SerCal != null && !nodeCache.Contains(serInputNode.SerCal))
                        nodeCache.Add(serInputNode.SerCal);

                    // Process entire level of nodes.
                    //inputNodes = [.. daysToEvaluate];
                    //daysToEvaluate.Clear();
                    //List<(int InputIndex, GetMostProfitableCropArgs Node)> totalOutputNodes = [];
                    //for (int i = 0; i < inputNodes.Count; ++i)
                    //{
                    //    (int Day, string SerCal) serInputNode = (inputNodes[i].Day, SerializeGameStateCalendar(inputNodes[i].Calendar, inputNodes[i].Day));
                    //    var outputNodes = ProcessNode(serInputNode.Day, serInputNode.SerCal, cheapestCrop.buyPrice, startingGold, startingTiles, crops, NumDays);
                    //    totalOutputNodes.AddRange(outputNodes.Select(node => (i, node)));
                    //}
                    //groupedOutputNodes = totalOutputNodes;
                }

                // Save output nodes.
                foreach (var group in groupedOutputNodes.GroupBy(x => x.InputIndex))
                {
                    var inputNode = inputNodes[group.Key];

                    foreach (var (InputIndex, OutputNode) in group)
                    {
                        // Merge output with input.
                        if (inputNode.Day > 1)
                            OutputNode.Calendar.Merge(inputNode.Calendar, startingDay: 1, endingDay: inputNode.Day - 1, deep: false);

                        if (OutputNode.Calendar.Wealth > bestWealth)
                        {
                            bestWealth = OutputNode.Calendar.Wealth;
                            bestCalendar = OutputNode.Calendar;
                        }

                        if (OutputNode.Day < NumDays)
                            daysToEvaluate.Enqueue(OutputNode);
                    }
                }

                //Console.WriteLine($"\n[GCSF] Nodes processed: {inputNodes.Count} / {debugNodeCount}.  Nodes in queue: {daysToEvaluate.Count},  Time: {debugStopwatch.Elapsed:mm\\:ss\\.f}");
            }

            processor.PrintStats();

            Console.WriteLine($"\n    [GCSF] DONE!  Nodes processed: {yielder.OperationCount}. Cache hits: {yielder.CacheHitCount}.  Time: {debugStopwatch.Elapsed:mm\\:ss\\.f}.  Crops: {crops.Count()}");
            Console.WriteLine($"    [GCSF] Crops: {string.Join(", ", crops)}\n");

            return Tuple.Create(bestWealth, bestCalendar);
        }

        internal static Queue<GetMostProfitableCropArgs> ProcessNode(int day, string serializedInputCalendar,
            double cheapestCropBuyPrice, double startingGold, int startingTiles, IEnumerable<Crop> crops, int numDays)
        {
            // Deserialize input calendar once.
            // Should use raw input calendar, but for some reason using the serialized version significantly reduces memory/runtime. Maybe GC issue?
            var inputCalendar = new GameStateCalendar(numDays, crops.ToDictionary(c => c.name), serializedInputCalendar);

            int availableTiles = inputCalendar.GameStates[day].FreeTiles;
            double availableGold = inputCalendar.GameStates[day].Wallet;

            var newDaysToEvaluate = new Queue<GetMostProfitableCropArgs>();

            foreach (var crop in crops)
            {
                bool thisCropScheduleCompleted = true;

                // OLD APPROACH
                // Tree data structure: make shallow read-only copy of previous days, and deep copy of current and future days since we mean to modify them.
                //var thisCropCalendar = new GameStateCalendar(args.Calendar, startingDay: day, endingDay: numDays + 1);

                // Deep copy of specified range only.
                var thisCropCalendar = new GameStateCalendar(inputCalendar, day, numDays + 1);

                // Calculate number of units to plant.
                int unitsCanAfford = crop.buyPrice != 0 ? ((int)(availableGold / crop.buyPrice)) : int.MaxValue;
                bool goldLimited = availableTiles != -1 ? availableTiles >= unitsCanAfford : true;
                int unitsToPlant = goldLimited ? unitsCanAfford : availableTiles;

                // Short-circuit number of units if too close to end of month for it to make money.
                if ((day + crop.timeToMaturity > numDays) || (crop.NumHarvests(day, numDays) == 1 && crop.buyPrice >= crop.sellPrice))
                    unitsToPlant = 0;

                if (unitsToPlant > 0)
                {
                    // Update game state based on the current purchase.
                    if (ReturnTilesAsap)
                        UpdateCalendar(thisCropCalendar, unitsToPlant, crop, day, numDays);
                    else
                        UpdateCalendar_HoldTiles(thisCropCalendar, unitsToPlant, crop, day, numDays);

                    // Queue up updating game state based on subsequent purchases.
                    int nextDay = MultiCrop ? day : day + 1;
                    for (int j = nextDay; j <= numDays; ++j)
                    {
                        // Be careful with increasing the threshold. In a past implementation, it counter-intuitively increased the state space from 2000 to 6000. In another implementation, it made the results incorrect.
                        //goldLowerLimit = Math.Max(InvestmentThreshold * MyCurrentValue(thisCropCalendar.GameStates[j]), goldLowerLimit);
                        //if (thisCropCalendar.GameStates[j].Wallet >= cheapestCrop.buyPrice && (thisCropCalendar.GameStates[j].FreeTiles == -1 || thisCropCalendar.GameStates[j].FreeTiles > 0))
                        if (thisCropCalendar.GameStates[j].Wallet >= cheapestCropBuyPrice && thisCropCalendar.GameStates[j].Wallet >= startingGold * GoldInvestmentThreshold
                            && (thisCropCalendar.GameStates[j].FreeTiles == -1 || thisCropCalendar.GameStates[j].FreeTiles > 0) && (thisCropCalendar.GameStates[j].FreeTiles == -1 || thisCropCalendar.GameStates[j].FreeTiles > startingTiles * TileInvestmentThresold))
                        {
                            thisCropScheduleCompleted = false;
                            newDaysToEvaluate.Enqueue(new GetMostProfitableCropArgs(j, thisCropCalendar));
                            break;
                        }
                    }
                }
                else
                    thisCropScheduleCompleted = true;

                // Choose today's crop based on the most profitable schedule
                double thisCropWealth = thisCropCalendar?.GameStates[numDays + 1].Wallet ?? 0;
            }

            return newDaysToEvaluate;
        }

        /// <summary>
        /// Return an APPROXIMATION of the best schedule and wealth.
        /// Accurate to within 0-15%.
        /// 
        /// Algorithm is a basic mathematical equation or heuristic. It basically checks what the best crop to plant the entire
        /// field with would be on any given day, given the available tiles and gold.
        /// Takes very little memory and CPU.
        /// </summary>
        private void GetBestSchedule_Strategy2(in int startDay, List<Crop> crops, GameStateCalendar calendar)
        {
            SortedSet<int> daysOfInterest = new SortedSet<int>() { startDay };

            // Find best crop schedule
            for (int day = startDay; day <= NumDays; ++day)
            {
                if (!daysOfInterest.Contains(day))
                    continue;

                var curGameState = calendar.GameStates[day];

                // Find best crop for this day
                double bestProfitMetric = 0;
                Crop bestCrop = null;
                int bestNumToPlant = 0;

                foreach (var crop in crops)
                {
                    double curProfitMetric = crop.CurrentProfit(day, curGameState.FreeTiles, curGameState.Wallet, NumDays, PaydayDelay, out int numToPlant);

                    if (curProfitMetric > bestProfitMetric)
                    {
                        bestProfitMetric = curProfitMetric;
                        bestCrop = crop;
                        bestNumToPlant = numToPlant;
                    }
                }

                // Add best crop's paydays
                if (bestCrop != null)
                {
                    if (ReturnTilesAsap)
                        UpdateCalendar(calendar, bestNumToPlant, bestCrop, day, NumDays);
                    else
                        UpdateCalendar_HoldTiles(calendar, bestNumToPlant, bestCrop, day, NumDays);

                    foreach (int harvestDay in bestCrop.HarvestDays(day, NumDays))
                    {
                        if (harvestDay <= NumDays)
                            daysOfInterest.Add(harvestDay + PaydayDelay);
                    }

                    day = MultiCrop ? day - 1 : day;
                }
            }

            return;
        }

        /// <summary>
        /// Update calendar to reflect planting this batch on this day. Returns tiles on day of harvest!
        /// </summary>
        private static void UpdateCalendar(GameStateCalendar calendar, in int unitsToPlant, in Crop crop, int day, in int numDays)
        {
            if (unitsToPlant <= 0)
                return;

            int availableTiles = calendar.GameStates[day].FreeTiles;

            // Modify current day state.
            double cost = unitsToPlant * crop.buyPrice;
            double sale = unitsToPlant * crop.sellPrice;
            PlantBatch plantBatch = new PlantBatch(crop, unitsToPlant, day, numDays);
            var harvestDays = plantBatch.HarvestDays;

            double cumulativeSale = 0;
            int curUnits = unitsToPlant;

            calendar.GameStates[day].DayOfInterest = true;

            // Update game state calendar based on today's crop purchase.
            for (int j = day; j <= calendar.NumDays + 1; ++j)
            {
                // Harvest day might increase tiles:
                if (plantBatch.HarvestDays.Contains(j))
                {
                    if (!plantBatch.Persistent && curUnits != 0)
                        curUnits = 0;
                }

                // Payday increases gold:
                if (plantBatch.HarvestDays.Contains(j - PaydayDelay))
                {
                    cumulativeSale += sale;
                    calendar.GameStates[j].DayOfInterest = true;
                }

                // Decrease tiles if plant isn't dead
                if (curUnits > 0)
                {
                    if (availableTiles != -1)
                        calendar.GameStates[j].FreeTiles = calendar.GameStates[j].FreeTiles - curUnits;

                    calendar.GameStates[j].Plants.Add(plantBatch);
                }

                // Modify gold
                calendar.GameStates[j].Wallet = calendar.GameStates[j].Wallet + cumulativeSale - cost;
            }
        }

        /// <summary>
        /// Update calendar to reflect planting this batch on this day. Returning tiles coincides with payday.
        /// </summary>
        private static void UpdateCalendar_HoldTiles(GameStateCalendar calendar, in int unitsToPlant, in Crop crop, int day, in int numDays)
        {
            if (unitsToPlant <= 0)
                return;

            int availableTiles = calendar.GameStates[day].FreeTiles;

            // Modify current day state.
            double cost = unitsToPlant * crop.buyPrice;
            double sale = unitsToPlant * crop.sellPrice;
            PlantBatch plantBatch = new PlantBatch(crop, unitsToPlant, day, numDays);
            var harvestDays = plantBatch.HarvestDays;

            double cumulativeSale = 0;
            int curUnits = unitsToPlant;

            calendar.GameStates[day].DayOfInterest = true;

            // Update game state calendar based on today's crop purchase.
            for (int j = day; j <= calendar.NumDays + 1; ++j)
            {
                if (plantBatch.HarvestDays.Contains(j - PaydayDelay))
                {
                    // Payday:

                    // Decrease tiles if plant is not dead
                    if (!plantBatch.Persistent)
                        curUnits = 0;

                    if (curUnits > 0)
                    {
                        calendar.GameStates[j].Plants.Add(plantBatch);

                        if (availableTiles != -1)
                            calendar.GameStates[j].FreeTiles = calendar.GameStates[j].FreeTiles - curUnits;
                    }

                    // Modify gold
                    cumulativeSale += sale;

                    calendar.GameStates[j].Wallet = calendar.GameStates[j].Wallet + cumulativeSale - cost;
                    calendar.GameStates[j].DayOfInterest = true;
                }
                else
                {
                    // Not payday:

                    // Decrease tiles if not dead
                    if (curUnits > 0)
                    {
                        if (availableTiles != -1)
                            calendar.GameStates[j].FreeTiles = calendar.GameStates[j].FreeTiles - curUnits;

                        calendar.GameStates[j].Plants.Add(plantBatch);
                    }

                    // Modify gold
                    calendar.GameStates[j].Wallet = calendar.GameStates[j].Wallet + cumulativeSale - cost;
                }
            }
        }

        /// <summary>
        /// Round a value to a given number of significant digits.
        /// Example: If 2 sig digits, 23,343 becomes 23,000. 563 becomes 560.
        /// </summary>
        private static double RoundToSignificantDigits(double value)
        {
            if (value == 0)
                return 0;

            double scale = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(value))) - (SignificantDigits - 1));

            var rounded = Math.Round(value / scale) * scale;

            // Clean up potential floating-point noise
            return Math.Round(rounded, SignificantDigits);
        }

        /// <summary>
        /// Serialize game state from the startingDay onward.
        /// </summary>
        internal static string SerializeGameStateCalendar(GameStateCalendar calendar, int startingDay, bool includePlants = false, bool round = true)
        {
            var serializedGameStateSb = new StringBuilder();
            int finalDay = calendar.NumDays + 1;

            for (int day = startingDay; day <= finalDay; ++day)
            {
                var gameStateDay = calendar.GameStates[day];

                if (gameStateDay.DayOfInterest || day == finalDay || day == startingDay)
                {
                    int roundedWallet = round ? (int) RoundToSignificantDigits(gameStateDay.Wallet) : (int) gameStateDay.Wallet;
                    int roundedTiles = round ? (int) RoundToSignificantDigits(gameStateDay.FreeTiles) : gameStateDay.FreeTiles;

                    serializedGameStateSb.Append($"{day}_{roundedWallet}_{roundedTiles}_");

                    if (includePlants)
                    {
                        for(int plantIndex = 0; plantIndex < gameStateDay.Plants.Count; ++plantIndex)
                        {
                            if (plantIndex > 0)
                                serializedGameStateSb.Append('-');

                            var plant = gameStateDay.Plants[plantIndex];
                            serializedGameStateSb.Append($"{plant.CropType};{plant.Count};{plant.PlantDay};{plant.NumDays}");
                        }
                    }

                    serializedGameStateSb.AppendLine();
                }
            }

            return serializedGameStateSb.ToString();
        }

        private Tuple<double, GameStateCalendar> MemoryFailure()
        {
            Console.WriteLine($"Error: canceled schedule generation due to high memory usage.");

            nodeCache.Clear();
            daysToEvaluate.Clear();

            GC.Collect();
            GC.WaitForPendingFinalizers(); // Ensures finalizers are run
            GC.Collect(); // Collects again to free objects finalized in the first pass

            return Tuple.Create(-2.0, new GameStateCalendar(NumDays, startingTiles, startingGold));
        }
    }
}
