using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace StardewCropCalculatorLibrary
{
    /// <summary> The state of our farm on every day. </summary>
    public class GameStateCalendar
    {
        public readonly int NumDays;

        /// <summary> The state of our farm on a particular day. Ie, how many plants, free tiles, and gold we have. </summary>
        public readonly SortedDictionary<int, GameState> GameStates = new SortedDictionary<int, GameState>();

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

        public GameStateCalendar(GameStateCalendar otherCalendar)
        {
            NumDays = otherCalendar.NumDays;

            for (int i = 1; i <= NumDays + 1; ++i)
                GameStates.Add(i, new GameState());

            Merge(otherCalendar, 1);
        }

        private void Merge(GameStateCalendar otherCalendar, int otherStartingDay)
        {
            for (int i = otherStartingDay; i <= NumDays + 1; ++i)
            {
                GameStates[i].Wallet = otherCalendar.GameStates[i].Wallet;
                GameStates[i].FreeTiles = otherCalendar.GameStates[i].FreeTiles;
                GameStates[i].DayOfInterest = otherCalendar.GameStates[i].DayOfInterest;

                GameStates[i].Plants.Clear();

                foreach (PlantBatch otherPlantBatch in otherCalendar.GameStates[i].Plants)
                    GameStates[i].Plants.Add(new PlantBatch(otherPlantBatch));
            }
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
    /// </summary>
    public class PlantBatch
    {
        public Crop CropType = null;
        public int Count = 0;
        public readonly SortedSet<int> HarvestDays = new SortedSet<int>();
        public bool Persistent => IsPersistent(CropType);

        public int PlantDay;

        public PlantBatch(Crop cropType, int cropCount, int plantDay)
        {
            CropType = cropType;
            Count = cropCount;
            PlantDay = plantDay;

            int harvestDate = plantDay + cropType.timeToMaturity;

            if (harvestDate <= 28)
                HarvestDays.Add(harvestDate);
            else
                return;

            while (harvestDate + cropType.yieldRate <= 28)
            {
                harvestDate += cropType.yieldRate;
                HarvestDays.Add(harvestDate);
            }
        }

        public PlantBatch(PlantBatch otherPlantBatch)
        {
            CropType = otherPlantBatch.CropType;
            Count = otherPlantBatch.Count;
            PlantDay = otherPlantBatch.PlantDay;

            foreach (int otherHarvestDay in otherPlantBatch.HarvestDays)
                HarvestDays.Add(otherHarvestDay);
        }

        private static bool IsPersistent(Crop crop)
        {
            // TODO: input numDays
            return crop.yieldRate > 0 && crop.yieldRate < 28;
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
        //public class GameStateTree
        //{
        //    public GameState Value { get; set; }
        //    public List<GameStateTree> Children { get; set; }

        //    public GameStateTree(GameState value)
        //    {
        //        Value = value;
        //        Children = new List<GameStateTree>();
        //    }

        //    public void AddChild(GameStateTree child)
        //    {
        //        Children.Add(child);
        //    }
        //}

        private class GetMostProfitableCropArgs
        {
            public int Day;
            public double GoldLowerLimit;
            public GameStateCalendar Calendar;

            public GetMostProfitableCropArgs(int day, double goldLowerLimit, GameStateCalendar calendar)
            {
                Day = day;
                GoldLowerLimit = goldLowerLimit;
                Calendar = calendar;
            }
        }

        // GameState cache
        private readonly Dictionary<string, Tuple<double, GameStateCalendar>> answerCache = new Dictionary<string, Tuple<double, GameStateCalendar>>();

        // Gold pruning: if gold is quite low we ignore it, to simplify the schedule. Expressed as fraction of starting gold. 0 means we process any amount of gold, and 1 means we ignore an amount equal to the starting gold.
        private static readonly double GoldInvestmentThreshold = 0.5;
        private static readonly double TileInvestmentThresold = 0.07;

        // Used to bucket cache results.
        private static readonly int SignificantDigits = 1;

        private static readonly bool UseCache = true;

        private int NumDays;

        private readonly List<Crop> Crops = new List<Crop>();

        private Queue<GetMostProfitableCropArgs> daysToEvaluate = new Queue<GetMostProfitableCropArgs>(3000);

        private int numOperationsStat = 0;
        private int numCacheHitsStat = 0;

        private int startingTiles = 0;
        private double startingGold = 0;

        private Crop cheapestCrop = null;

        private double memoryThreshold = 1.35;

        public GameStateCalendarFactory(int numDays, List<Crop> crops)
        {
            NumDays = numDays;
            Crops = crops;

            // Find cheapest crop
            if (Crops != null && Crops.Count > 0)
            {
                cheapestCrop = Crops[0];
                foreach (var crop in Crops)
                    cheapestCrop = crop.buyPrice < cheapestCrop.buyPrice ? crop : cheapestCrop;
            }
        }

        /// <summary>
        /// Return the optimial schedule.
        /// </summary>
        public async Task<Tuple<double, GameStateCalendar>> GetMostProfitableCrop(int availableTiles, double availableGold)
        {
            answerCache.Clear();
            daysToEvaluate.Clear();
            numOperationsStat = 0;
            numCacheHitsStat = 0;

            // If tiles are infinite but gold is still finite, then we can still calculate finite end wealth.
            if (availableTiles <= 0)
                availableTiles = -1;

            var calendar = new GameStateCalendar(NumDays, availableTiles, availableGold);

            startingGold = availableGold;
            startingTiles = availableTiles;

            daysToEvaluate.Enqueue(new GetMostProfitableCropArgs(1, cheapestCrop.buyPrice, calendar));

            var answer = await GetMostProfitableCropIterative();

            answerCache.Clear();
            daysToEvaluate.Clear();
            numOperationsStat = 0;
            numCacheHitsStat = 0;

            return answer;
        }

        /// <summary>
        /// Return the maximum amount of gold you can end the season with, and the schedule that accompanies it.
        /// </summary>
        private async Task<Tuple<double, GameStateCalendar>> GetMostProfitableCropIterative()
        {
            List<GameStateCalendar> completedCalendars = new List<GameStateCalendar>(300);
            double bestWealth = 0;
            GameStateCalendar bestCalendar = null;

            // Evaluate all possible schedules.
            // Use a breadth-first approach with the game state tree.
            while (daysToEvaluate.Count > 0)
            {
                await Task.Yield();

                // Cancel operation and garbage-collect if above memory limit.
                if (numOperationsStat % 500 == 0 && CheckMemoryLimit())
                {
                    Console.WriteLine($"Error: canceled schedule generation due to high memory usage.");

                    completedCalendars.Clear();
                    answerCache.Clear();
                    daysToEvaluate.Clear();

                    GC.Collect();
                    GC.WaitForPendingFinalizers(); // Ensures finalizers are run
                    GC.Collect(); // Collects again to free objects finalized in the first pass

                    return Tuple.Create(-2.0, new GameStateCalendar(NumDays, startingTiles, startingGold));
                }

                var args = daysToEvaluate.Dequeue();
                var day = args.Day;
                var goldLowerLimit = args.GoldLowerLimit;
                GameStateCalendar todaysCalendar = args.Calendar;

                int numDays = todaysCalendar.NumDays;
                int availableTiles = todaysCalendar.GameStates[day].FreeTiles;
                double availableGold = todaysCalendar.GameStates[day].Wallet;
                string serializedInputGameState = SerializeGameStateCalendar(todaysCalendar, day);

                // Check cache for quick answer
                if (UseCache && answerCache.TryGetValue(serializedInputGameState, out var wealthSchedulePair))
                {
                    ++numCacheHitsStat;
                    continue;
                }

                ++numOperationsStat;

                foreach (var crop in Crops)
                {
                    bool thisCropScheduleCompleted = true;

                    GameStateCalendar thisCropCalendar = new GameStateCalendar(todaysCalendar);

                    // Calculate number of units to plant.
                    int unitsCanAfford = ((int)(availableGold / crop.buyPrice));
                    bool goldLimited = availableTiles != -1 ? availableTiles >= unitsCanAfford : true;
                    int unitsToPlant = goldLimited ? unitsCanAfford : availableTiles;

                    // Short-circuit number of units if too close to end of month for it to make money.
                    if ((day + crop.timeToMaturity > numDays) || (crop.NumHarvests(day, numDays) == 1 && crop.buyPrice >= crop.sellPrice))
                        unitsToPlant = 0;

                    if (unitsToPlant > 0)
                    {
                        // Update game state based on the current purchase.
                        UpdateCalendar(thisCropCalendar, unitsToPlant, crop, availableTiles, day);

                        // Queue up updating game state based on subsequent purchases.
                        for (int j = day + 1; j <= numDays; ++j)
                        {
                            // Be careful with increasing the threshold. In a past implementation, it counter-intuitively increased the state space from 2000 to 6000. In another implementation, it made the results incorrect.
                            //goldLowerLimit = Math.Max(InvestmentThreshold * MyCurrentValue(thisCropCalendar.GameStates[j]), goldLowerLimit);

                            //if (thisCropCalendar.GameStates[j].Wallet >= cheapestCrop.buyPrice && (thisCropCalendar.GameStates[j].FreeTiles == -1 || thisCropCalendar.GameStates[j].FreeTiles > 0))
                            if (thisCropCalendar.GameStates[j].Wallet >= cheapestCrop.buyPrice && thisCropCalendar.GameStates[j].Wallet >= startingGold * GoldInvestmentThreshold
                                && (thisCropCalendar.GameStates[j].FreeTiles == -1 || thisCropCalendar.GameStates[j].FreeTiles > 0) && (thisCropCalendar.GameStates[j].FreeTiles == -1 || thisCropCalendar.GameStates[j].FreeTiles > startingTiles * TileInvestmentThresold))
                            {
                                thisCropScheduleCompleted = false;
                                daysToEvaluate.Enqueue(new GetMostProfitableCropArgs(j, 0, thisCropCalendar));
                                break;
                            }
                        }
                    }
                    else
                        thisCropScheduleCompleted = true;

                    // If no further action can be taken for all crops, then a schedule has been completed!
                    if (thisCropScheduleCompleted)
                    {
                        completedCalendars.Add(thisCropCalendar);

                        double thisCropWealth = thisCropCalendar?.GameStates[29].Wallet ?? 0;

                        if (thisCropWealth > bestWealth)
                        {
                            bestWealth = thisCropWealth;
                            bestCalendar = thisCropCalendar;
                        }

                        if (!answerCache.ContainsKey(serializedInputGameState))
                            answerCache[serializedInputGameState] = Tuple.Create(thisCropWealth, thisCropCalendar);
                    }

                } // Crops loop
            } // daysToEvaluate loop

            return Tuple.Create(bestWealth, bestCalendar);
        }

        private static void UpdateCalendar(GameStateCalendar calendar, int unitsToPlant, Crop crop, int availableTiles, int day)
        {
            if (unitsToPlant <= 0)
                return;

            // Modify current day state.
            double cost = unitsToPlant * crop.buyPrice;
            double sale = unitsToPlant * crop.sellPrice;

            calendar.GameStates[day].Wallet = calendar.GameStates[day].Wallet - cost;
            if (availableTiles != -1)
                calendar.GameStates[day].FreeTiles = calendar.GameStates[day].FreeTiles - unitsToPlant;
            calendar.GameStates[day].DayOfInterest = true;

            PlantBatch plantBatch = new PlantBatch(crop, unitsToPlant, day);
            var harvestDays = plantBatch.HarvestDays;
            calendar.GameStates[day].Plants.Add(plantBatch);

            double cumulativeSale = 0;
            int curUnits = unitsToPlant;

            // Update game state calendar based on today's crop purchase.
            for (int j = day; j <= calendar.NumDays; ++j)
            {
                if (plantBatch.HarvestDays.Contains(j))
                {
                    // Payday:

                    // Decrease tiles if plant is not dead
                    if (!plantBatch.Persistent)
                        curUnits = 0;

                    if (curUnits > 0)
                    {
                        calendar.GameStates[j + 1].Plants.Add(new PlantBatch(crop, unitsToPlant, day));

                        if (availableTiles != -1)
                            calendar.GameStates[j + 1].FreeTiles = calendar.GameStates[j + 1].FreeTiles - curUnits;
                    }

                    // Modify gold
                    cumulativeSale += sale;

                    calendar.GameStates[j + 1].Wallet = calendar.GameStates[j + 1].Wallet + cumulativeSale - cost;
                    calendar.GameStates[j + 1].DayOfInterest = true;
                }
                else
                {
                    // Not payday:

                    // Decrease tiles if not dead
                    if (curUnits > 0)
                    {
                        if (availableTiles != -1)
                            calendar.GameStates[j + 1].FreeTiles = calendar.GameStates[j + 1].FreeTiles - curUnits;

                        calendar.GameStates[j + 1].Plants.Add(new PlantBatch(crop, unitsToPlant, day));
                    }

                    // Modify gold
                    calendar.GameStates[j + 1].Wallet = calendar.GameStates[j + 1].Wallet + cumulativeSale - cost;
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

            return Math.Round(value / scale) * scale;
        }

        /// <summary>
        /// Serialize game state from the startingDay onward.
        /// </summary>
        private static string SerializeGameStateCalendar(GameStateCalendar calendar, int startingDay = 0)
        {
            StringBuilder serializedGameStateSb = new StringBuilder();
            int finalDay = calendar.NumDays + 1;

            for (int day = startingDay; day <= finalDay; ++day)
            {
                var gameStateDay = calendar.GameStates[day];

                if (gameStateDay.DayOfInterest || day == finalDay)
                    serializedGameStateSb.AppendLine($"{day}_{RoundToSignificantDigits(gameStateDay.Wallet)}_{RoundToSignificantDigits(gameStateDay.FreeTiles)}");
            }

            return serializedGameStateSb.ToString();
        }

        private static List<int> GetHarvestDays(int plantDay, Crop crop)
        {
            List<int> harvestDays = new List<int>();

            int harvestDate = plantDay + crop.timeToMaturity;

            if (harvestDate <= 28)
                harvestDays.Add(harvestDate);
            else
                return harvestDays;

            while (harvestDate + crop.yieldRate <= 28)
            {
                harvestDate += crop.yieldRate;
                harvestDays.Add(harvestDate);
            }

            return harvestDays;
        }

        private bool CanPlantInFuture(int currentDay, GameStateCalendar calendar)
        {
            int numDays = calendar.NumDays;

            for (int day = currentDay + 1; day <= numDays; ++day)
            {
                double availableGold = calendar.GameStates[day].Wallet;
                int availableTiles = calendar.GameStates[day].FreeTiles;

                if (availableGold > 0 && (availableTiles > 0 || availableTiles == -1))
                {
                    // Check if any crop can be planted on this day
                    foreach (var crop in Crops)
                    {
                        if (day + crop.timeToMaturity <= numDays)
                        {
                            int unitsCanAfford = (int)(availableGold / crop.buyPrice);

                            if (unitsCanAfford > 0)
                                return true; // Planting is possible in the future
                        }
                    }
                }
            }

            return false; // No planting actions possible in future days
        }

        private bool CheckMemoryLimit()
        {
            long memoryInBytes = GC.GetTotalMemory(false);
            double memoryInGB = memoryInBytes / (1024.0 * 1024.0 * 1024.0);

            Console.WriteLine($"Stats: Number of operations: {numOperationsStat.ToString("N0")}, cacheHits: {numCacheHitsStat}");
            Console.WriteLine($"Memory usage: {memoryInGB:F2} GB");

            if (memoryInGB >= memoryThreshold)
                return true;
            else
                return false;
        }
    }
}
