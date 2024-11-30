using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace StardewCropCalculatorLibrary
{
    internal class PlantScheduleFactory2
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
                for(int i = otherStartingDay; i <= NumDays + 1; ++i)
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
        }

        // GameState cache
        private readonly Dictionary<int, Dictionary<double, Dictionary<int, Tuple<double, GameStateCalendar>>>> answerCache = new Dictionary<int, Dictionary<double, Dictionary<int, Tuple<double, GameStateCalendar>>>>();

        private int NumDays;

        public PlantScheduleFactory2(int numDays)
        {
            NumDays = numDays;
        }

        /// <summary>
        /// Return the optimial schedule.
        /// </summary>
        public async Task<Tuple<double, GameStateCalendar>> GetMostProfitableCrop(int day, List<Crop> crops, int availableTiles, double availableGold)
        {
            // If tiles are infinite but gold is finite, it's still sane to calculate profit in gold.
            if (availableTiles <= 0)
                availableTiles = 1000000000;

            var calendar = new GameStateCalendar(NumDays, availableTiles, availableGold);

            // Find cheapest crop
            Crop cheapestCrop = crops[0];
            foreach (var crop in crops)
                cheapestCrop = crop.buyPrice < cheapestCrop.buyPrice ? crop : cheapestCrop;

            var wealth = await GetMostProfitableCropRecursive(day, crops, cheapestCrop.buyPrice, calendar);

            return Tuple.Create(wealth, calendar);
        }

        /// <summary>
        /// Return the maximum amount of gold you can end the season with, and the schedule accompanies it.
        /// </summary>
        private async Task<double> GetMostProfitableCropRecursive(int day, List<Crop> crops, double goldLowerLimit, GameStateCalendar calendar)
        {
            await Task.Yield();

            double bestWealth = 0;
            GameStateCalendar bestCalendar = null;

            int numDays = calendar.NumDays;
            int availableTiles = calendar.GameStates[day].FreeTiles;
            double availableGold = calendar.GameStates[day].Wallet;

            // Check cache for quick answer
            if (answerCache != null && answerCache.TryGetValue(day, out var goldDict))
            {
                if (goldDict != null && goldDict.TryGetValue(availableGold, out var tileDict))
                {
                    if (tileDict != null && tileDict.TryGetValue(availableTiles, out var wealthSchedulePair))
                    {
                        double cachedWealth = wealthSchedulePair.Item1;
                        GameStateCalendar cachedCalendar = wealthSchedulePair.Item2;

                        if (cachedCalendar != null)
                        {
                            //Console.WriteLine($"[DEBUG] Accessed cache!!!! Day: {day}, availableGold: {availableGold}, availableTiles: {availableTiles}, ");
                            calendar.Merge(cachedCalendar, day);
                            return cachedWealth;
                        }
                    }
                }
            }

            foreach (var crop in crops)
            {
                var localCalendar = new GameStateCalendar(calendar);
                double endWealth = 0;

                // Calculate number of units to plant.
                int unitsCanAfford = ((int)(availableGold / crop.buyPrice));
                bool goldLimited = availableTiles >= unitsCanAfford;
                int unitsToPlant = goldLimited ? unitsCanAfford : availableTiles;

                // Short-circuit number of units if too close to end of month for it to make money.
                if ((day + crop.timeToMaturity > 28)
                    || (crop.NumHarvests(day, numDays) == 1 && crop.buyPrice >= crop.sellPrice))
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
                                localCalendar.GameStates[j + 1].FreeTiles = localCalendar.GameStates[j + 1].FreeTiles - curUnits;
                                localCalendar.GameStates[j + 1].Plants.Add(new PlantBatch(crop, unitsToPlant, day));
                            }

                            // Modify gold
                            localCalendar.GameStates[j + 1].Wallet = localCalendar.GameStates[j + 1].Wallet + cumulativeSale - cost;
                        }
                    }

                    for (int j = day + 1; j <= numDays; ++j)
                    {
                        if (localCalendar.GameStates[j].Wallet >= goldLowerLimit && localCalendar.GameStates[j].FreeTiles > 0)
                        {
                            await GetMostProfitableCropRecursive(j, crops, goldLowerLimit, localCalendar);
                            break;
                        }
                    }
                }

                endWealth = localCalendar.GameStates[29].Wallet;

                // Save best crop
                if (endWealth > bestWealth)
                {
                    bestWealth = endWealth;
                    bestCalendar = localCalendar;
                }
            }

            // Update cache if the real deal
            if (!answerCache.ContainsKey(day))
                answerCache[day] = new Dictionary<double, Dictionary<int, Tuple<double, GameStateCalendar>>>();
            if (!answerCache[day].ContainsKey(availableGold))
                answerCache[day][availableGold] = new Dictionary<int, Tuple<double, GameStateCalendar>>();
            if (!answerCache[day][availableGold].ContainsKey(availableTiles) || bestWealth > answerCache[day][availableGold][availableTiles].Item1)
                answerCache[day][availableGold][availableTiles] = Tuple.Create(bestWealth, bestCalendar);

            if (bestCalendar != null)
                calendar.Merge(bestCalendar, day);

            return bestWealth;
        }

        private static bool IsPersistent(Crop crop)
        {
            // TODO: input numDays
            return crop.yieldRate > 0 && crop.yieldRate < 28;
        }
    }
}
