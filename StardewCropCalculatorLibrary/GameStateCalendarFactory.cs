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
        public int NumDays;

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

        public void Merge(GameStateCalendar otherCalendar, int otherStartingDay)
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
    }

    /// <summary>
    /// This scheduler algorithm is an approximate gamestate simulation. Thus it runs slow.
    /// The benefit is it can deal with limited tiles, or any input.
    /// </summary>
    public class GameStateCalendarFactory
    {
        // GameState cache
        private readonly Dictionary<string, Tuple<double, GameStateCalendar>> answerCache = new Dictionary<string, Tuple<double, GameStateCalendar>>();

        /// <summary> Percentage of total wealth that must be met in order to bother investing a certain amount of money. </summary>
        private static readonly double InvestmentThreshold = 0.05;

        /// <summary> Percentage of farm size that must be available in order to bother trying to invest. </summary>
        private static readonly double TileThreshold = 0.05;
        private static readonly int SignificantDigits = 2;
        private static readonly bool UseCache = true;

        private int NumDays;

        private readonly List<Crop> Crops = new List<Crop>();

        private int numOperationsStat = 0;
        private int numCacheHitsStat = 0;

        public GameStateCalendarFactory(int numDays, List<Crop> crops)
        {
            NumDays = numDays;
            Crops = crops;
        }

        /// <summary>
        /// Return the optimial schedule.
        /// </summary>
        public async Task<Tuple<double, GameStateCalendar>> GetMostProfitableCrop(int day, List<Crop> crops, int availableTiles, double availableGold)
        {
            answerCache.Clear();
            numOperationsStat = 0;
            numCacheHitsStat = 0;

            // If tiles are infinite but gold is still finite, then we can still calculate finite end wealth.
            if (availableTiles <= 0)
                availableTiles = -1;

            var calendar = new GameStateCalendar(NumDays, availableTiles, availableGold);

            // Find cheapest crop
            Crop cheapestCrop = crops[0];
            foreach (var crop in crops)
                cheapestCrop = crop.buyPrice < cheapestCrop.buyPrice ? crop : cheapestCrop;

            var wealth = await GetMostProfitableCropRecursive(day, crops, cheapestCrop.buyPrice, calendar);

            Console.WriteLine($"Stats: Number of operations: {numOperationsStat.ToString("N0")}, cacheHits: {numCacheHitsStat}");

            return Tuple.Create(wealth, calendar);
        }

        /// <summary>
        /// Return the maximum amount of gold you can end the season with, and the schedule accompanies it.
        /// </summary>
        private async Task<double> GetMostProfitableCropRecursive(int day, List<Crop> crops, double goldLowerLimit, GameStateCalendar calendar)
        {
            await Task.Yield();

            ++numOperationsStat;

            double bestWealth = 0;
            GameStateCalendar bestCalendar = null;

            int numDays = calendar.NumDays;
            int availableTiles = calendar.GameStates[day].FreeTiles;
            double availableGold = calendar.GameStates[day].Wallet;
            string serializedInputGameState = SerializeGameStateCalendar(calendar, day);

            // Check cache for quick answer
            if (UseCache && answerCache.TryGetValue(serializedInputGameState, out var wealthSchedulePair))
            {
                ++numCacheHitsStat;

                calendar.Merge(wealthSchedulePair.Item2, day);
                return wealthSchedulePair.Item1;
            }

            foreach (var crop in crops)
            {
                var localCalendar = new GameStateCalendar(calendar);
                double endWealth = 0;

                // Calculate number of units to plant.
                int unitsCanAfford = ((int)(availableGold / crop.buyPrice));
                bool goldLimited = availableTiles != -1 ? availableTiles >= unitsCanAfford : true;
                int unitsToPlant = goldLimited ? unitsCanAfford : availableTiles;

                // Short-circuit number of units if too close to end of month for it to make money.
                if ((day + crop.timeToMaturity > 28) || (crop.NumHarvests(day, numDays) == 1 && crop.buyPrice >= crop.sellPrice))
                    unitsToPlant = 0;

                // Modify day's game state from what it was previously according to the new crop being planted.
                // Example: Day 8 and so on may have had 2000 gold and 15 tiles, because of crops we planted on day 1.
                // So if we plant a new crop in the above code on day 8, we want to decrease the tiles and gold for day 8 and so on.
                // Until the crop is harvested and perhaps dies at 8 + x, at which point we increase our tiles and gold for day 8 + x and so on.
                if (unitsToPlant > 0)
                {
                    // Modify current day state.
                    double cost = unitsToPlant * crop.buyPrice;
                    double sale = unitsToPlant * crop.sellPrice;

                    localCalendar.GameStates[day].Wallet = localCalendar.GameStates[day].Wallet - cost;
                    if (availableTiles != -1)
                        localCalendar.GameStates[day].FreeTiles = localCalendar.GameStates[day].FreeTiles - unitsToPlant;
                    localCalendar.GameStates[day].DayOfInterest = true;

                    PlantBatch plantBatch = new PlantBatch(crop, unitsToPlant, day);
                    var harvestDays = plantBatch.HarvestDays;
                    localCalendar.GameStates[day].Plants.Add(plantBatch);

                    double cumulativeSale = 0;
                    int curUnits = unitsToPlant;

                    for (int j = day; j <= numDays; ++j)
                    {
                        if (plantBatch.HarvestDays.Contains(j))
                        {
                            // Payday:

                            // Decrease tiles if plant is not dead
                            if (!plantBatch.Persistent)
                                curUnits = 0;

                            if (curUnits > 0)
                            {
                                localCalendar.GameStates[j + 1].Plants.Add(new PlantBatch(crop, unitsToPlant, day));

                                if (availableTiles != -1)
                                    localCalendar.GameStates[j + 1].FreeTiles = localCalendar.GameStates[j + 1].FreeTiles - curUnits;
                            }

                            // Modify gold
                            cumulativeSale += sale;

                            localCalendar.GameStates[j + 1].Wallet = localCalendar.GameStates[j + 1].Wallet + cumulativeSale - cost;
                            localCalendar.GameStates[j + 1].DayOfInterest = true;
                        }
                        else
                        {
                            // Not payday:

                            // Decrease tiles if not dead
                            if (curUnits > 0)
                            {
                                if (availableTiles != -1)
                                    localCalendar.GameStates[j + 1].FreeTiles = localCalendar.GameStates[j + 1].FreeTiles - curUnits;

                                localCalendar.GameStates[j + 1].Plants.Add(new PlantBatch(crop, unitsToPlant, day));
                            }

                            // Modify gold
                            localCalendar.GameStates[j + 1].Wallet = localCalendar.GameStates[j + 1].Wallet + cumulativeSale - cost;
                        }
                    }

                    for (int j = day + 1; j <= numDays; ++j)
                    {
                        // Base case: raise investment threshold the richer we get, to avoid complicating schedule with trivial investments

                        //goldLowerLimit = Math.Max(InvestmentThreshold * MyCurrentValue(localCalendar.GameStates[j]), goldLowerLimit);

                        // TODO: experiment with increasing these limits to avoid time-consuming trivial purchases.
                        // For some reason, raising the tile limit in my last test CAUSED trivial purchases. (Otherwise correct with no perf change) (1000g, inf tiles)
                        // Raising gold thresh caused a perf decrease. (Otherwise correct, and no interstitial purchases)
                        if (localCalendar.GameStates[j].Wallet > 0 && (localCalendar.GameStates[j].FreeTiles > 0 || localCalendar.GameStates[j].FreeTiles == -1))
                        {
                            await GetMostProfitableCropRecursive(j, crops, 0, localCalendar);
                            break;
                        }
                    }
                }

                endWealth = localCalendar.GameStates[29].Wallet;

                // Save best crop
                if (bestCalendar == null || endWealth > bestWealth)
                {
                    bestWealth = endWealth;
                    bestCalendar = localCalendar;
                }
            }

            // Update cache. Use key aspects of input game state.
            if (UseCache && (!answerCache.ContainsKey(serializedInputGameState) || bestWealth > answerCache[serializedInputGameState].Item1))
                answerCache[serializedInputGameState] = Tuple.Create(bestWealth, bestCalendar);

            calendar.Merge(bestCalendar, day);

            return bestWealth;
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
    }
}
