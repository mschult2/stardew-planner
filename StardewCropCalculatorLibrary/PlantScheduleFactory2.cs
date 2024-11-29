using System;
using System.Collections.Generic;
using System.Linq;
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
            public SortedDictionary<int, GameState> GameStates = new SortedDictionary<int, GameState>();

            public GameStateCalendar(int numDays, int availableTiles, double availableGold)
            {
                NumDays = numDays;

                // Adding one more day in case a crop is harvested on the last day.
                // In this case, we don't get our payday until the following day. So technically
                // that following day may have a state we care about, ie a larger balance.
                for(int i = 1; i <= numDays + 1; ++i)
                    GameStates.Add(i, new GameState());

                GameStates[1].Wallet = availableGold;
                GameStates[1].FreeTiles = availableTiles;
            }

            public void Merge(GameStateCalendar otherCalendar, int otherStartingDay)
            {
                for(int i = otherStartingDay; i <= NumDays; ++i)
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
            public List<PlantBatch> Plants = new List<PlantBatch>();

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
            public SortedSet<int> HarvestDays = new SortedSet<int>();
            public bool Persistent => IsPersistent(CropType);

            private int PlantDay;

            public PlantBatch(Crop cropType, int cropCount, int plantDay)
            {
                CropType = cropType;
                Count = cropCount;
                PlantDay = plantDay;

                int harvestDate = plantDay + cropType.timeToMaturity;
                HarvestDays.Add(harvestDate);

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

        public int NumDays;

        public PlantScheduleFactory2(int numDays)
        {
            NumDays = numDays;
        }

        /// <summary>
        /// Return the most profitable crop to plant on day x. Also returns the cumulative profit it fetches.
        /// </summary>
        public double GetMostProfitableCrop(int day, List<Crop> crops, int availableTiles, double availableGold, out GameStateCalendar calendar)
        {
            calendar = new GameStateCalendar(NumDays, availableTiles, availableGold);

            // ONLY TEMP CROPS!!
            foreach (var crop in crops)
            {
                if (IsPersistent(crop))
                {
                    Console.WriteLine("ERROR: remove persistent crops.");
                    return 0;
                }
            }

            // Find cheapest crop
            Crop cheapestCrop = crops[0];
            foreach (var crop in crops)
                cheapestCrop = crop.buyPrice < cheapestCrop.buyPrice ? crop : cheapestCrop;

            return GetMostProfitableCropRecursive(day, crops, cheapestCrop.buyPrice, calendar);
        }

        /// <summary>
        /// Return the most profitable crop to plant on day x. Also returns the cumulative profit it fetches.
        /// </summary>
        private double GetMostProfitableCropRecursive(in int day, in List<Crop> crops, in double goldLowerLimit, GameStateCalendar calendar)
        {
            double bestProfit = 0;

            int numDays = calendar.NumDays;
            int availableTiles = calendar.GameStates[day].FreeTiles;
            double availableGold = calendar.GameStates[day].Wallet;

            foreach (var crop in crops)
            {
                var localCalendar = new GameStateCalendar(numDays, availableTiles, availableGold);
                double profit = 0;

                // Calculate number of units to plant.
                int unitsCanAfford = ((int)(availableGold / crop.buyPrice));
                bool goldLimited = availableTiles >= unitsCanAfford;
                int unitsToPlant = goldLimited ? unitsCanAfford : availableTiles;

                // Calculate current day ending state.
                localCalendar.GameStates[day].FreeTiles = availableTiles - unitsToPlant;
                localCalendar.GameStates[day].Wallet = availableGold - (unitsToPlant * crop.buyPrice);
                localCalendar.GameStates[day].DayOfInterest = true;
                localCalendar.GameStates[day].Plants.Add(new PlantBatch(crop, unitsToPlant, day));

                // TODO: Calculate state for ALL days in the calendar afaik.

                // Calculate payday.
                int nextDay = day + crop.timeToMaturity + 1;

                if (nextDay <= numDays + 1)
                {
                    // Calculate payday profit.
                    profit = unitsToPlant * crop.sellPrice - unitsToPlant * crop.buyPrice;

                    // Calculate payday starting state.
                    localCalendar.GameStates[nextDay].FreeTiles = availableTiles;
                    localCalendar.GameStates[nextDay].Wallet = availableGold + profit;

                    if (nextDay <= numDays - 1 && localCalendar.GameStates[nextDay].FreeTiles > 0 && localCalendar.GameStates[nextDay].Wallet >= goldLowerLimit)
                    {
                        profit += GetMostProfitableCropRecursive(nextDay, crops, goldLowerLimit, localCalendar);
                    }
                }

                // Save best crop
                if (profit > bestProfit)
                {
                    bestProfit = profit;
                    calendar.Merge(localCalendar, day);
                }
            }

            return bestProfit;
        }

        private static bool IsPersistent(Crop crop)
        {
            // TODO: input numDays
            return crop.yieldRate > 0 && crop.yieldRate < 28;
        }
    }
}
