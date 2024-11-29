using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;

namespace StardewCropCalculatorLibrary
{
    public class PlantScheduleFactory
    {
        /// <summary>
        /// The number of days to schedule.  Generally the length of a season, or 28 days.
        /// </summary>
        public readonly int numDays;

        #region MemoTable This table holds the best planting schedule, as well as additional statistics about each day. Indexed by day; 0th element is unused.
        struct MemoTable
        {
            // The most profitable crop to plant on a particular day.
            public PlantSchedule schedule;

            /// The maximum CUMULATIVE investment multiple that can be obtained from planting this crop on a particular day. (E.g., 3x means tripling you money.)
            /// Accounts for reinvestment of interest.
            public double[] cumMultiple;
        }
        #endregion

        private MemoTable memo;

        private static readonly bool MultipleCropSupport = true;

        // After buying one crop type, there may be a remainder of gold left. If MultipleCropSupport is enabled, we'll use this buy other, cheaper crop types.
        // This is the threshold, as a fraction of initial gold, under which we don't both buying more crops.
        // 0 means always spend the remainder if possible. 1 means never use the remainder (same as setting MultipleCropSupport to false).
        private static readonly double RemainderThreshold = 0.1f;

        public PlantScheduleFactory(int numDays)
        {
            if (numDays < 1) AppUtils.Fail("PlantScheduleFactory(numDays) - numDays must be greater than 0");

            this.numDays = numDays;

            // Initialize memo table
            memo.schedule = new PlantSchedule(numDays);

            memo.cumMultiple = new double[numDays + 1]; // array goes from day 0 (unused) to day numDays for ease of access
            memo.cumMultiple = memo.cumMultiple.Select(x => 1.0).ToArray(); // If you don't plant, your money multiplies by 1x (no change)
        }

        /// <summary>
        /// Calculates the best crop to plant on each day.
        /// Uses a dynamic programming algorithm which memoizes the best schedule by iterating through it backward.
        /// </summary>
        /// <param name="crops">The crops which are available to be purchased</param>
        /// <param name="schedule">Outputs the best schedule</param>
        /// <param name="day">The day to start planting on</param>
        /// <returns></returns>
        public double GetBestSchedule(List<Crop> crops, out PlantSchedule schedule)
        {
            // Find the best crop for each day, starting from end of season.
            for (int day = numDays; day > 0; --day)
            {
                foreach (var crop in crops)
                {
                    int numHarvests = crop.NumHarvests(day, numDays);
                    var cropMultiple = crop.InvestmentMultiple(day, numDays); // sell price divided by cost

                    // Base case: no harvest
                    if (crop.NumHarvests(day, numDays) < 1)
                        continue;

                    // Calculate the cumulative investment multiple of the planting.
                    double cumInvestmentMultiple = 0;
                    var harvestDay = day + crop.timeToMaturity;
                    for (int harvest = 1; harvest <= numHarvests; ++harvest)
                    {
                        var nextPlantDay = harvestDay + 1; // Money comes in the day after a harvest
                        var nextPlantDayCumMultiple = nextPlantDay > numDays ? 1 : memo.cumMultiple[nextPlantDay];

                        cumInvestmentMultiple += (cropMultiple / numHarvests) * nextPlantDayCumMultiple;

                        harvestDay += crop.yieldRate;
                    }

                    // Update best crop for the current day.
                    if (cumInvestmentMultiple > memo.cumMultiple[day])
                    {
                        memo.cumMultiple[day] = cumInvestmentMultiple;
                        memo.schedule.SetCrop(day, crop);
                    }

                    if (Double.IsPositiveInfinity(cumInvestmentMultiple))
                        throw new Exception("Cumulative investment multiple is too large of a number!");
                }
            }

            // Return memo, which is now filled out with the best schedule.
            schedule = new PlantSchedule(memo.schedule);
            return memo.cumMultiple[1];
        }

        public bool GetNewSchedule(List<Crop> crops, int availableTiles, double availableGold, out bool[] plantingDays, out DayDetails[] scheduleDetails, out PlantSchedule schedule)
        {
            Console.WriteLine($"[GetNewSchedule] availableTiles {availableTiles}, availableGold: {availableGold}");

            schedule = new PlantSchedule(numDays);
            plantingDays = new bool[numDays + 1];
            scheduleDetails = new DayDetails[numDays + 1];

            SortedDictionary<int, double> payDays = new SortedDictionary<int, double>();
            payDays.Add(1, 0);

            List<Plant> plants = new List<Plant>();

            // TODO: should probably save sets of Plants, rather than every individual one.

            while (payDays != null && payDays.Count > 0)
            {
                var payDay = payDays.First();
                int day = payDay.Key;
                availableGold = payDay.Value + availableGold;

                // Remove crops that died
                List<Plant> plantsToRemove = new List<Plant>();
                foreach (var plant in plants)
                {
                    if (plant.Crop.name != "Radish")
                        if (plant.HarvestDays.Count <= 1 && plant.HarvestDays[0] <= day)
                            plantsToRemove.Add(plant);
                }
                foreach (var plantToRemove in plantsToRemove)
                    plants.Remove(plantToRemove);

                var crop = MostProfitableCrop(day, numDays, crops, availableTiles, availableGold, out double cumulativeProfit, out int numToPlant, out SortedDictionary<int, double> nextPayDays);

                if (crop != null && cumulativeProfit > 0)
                {
                    schedule.SetCrop(day, crop);
                    plantingDays[day] = true;
                    scheduleDetails[day] = new DayDetails() { cropsNumberToPlant = new List<int>() { numToPlant } };

                    availableTiles -= numToPlant;

                    // Save recurring crop for next time
                    for (int j = 0; j < numToPlant; ++j)
                        plants.Add(new Plant(crop, day, numDays, crop.timeToMaturity, crop.yieldRate));

                    // TODO: compute next day, even if its a harvest for a persistent crop. Since that's a day we get money, so it's still of interest.
                }

                if (day == 1)
                    Console.WriteLine($"[GetNewSchedule] PROFIT: {cumulativeProfit}");

                Console.WriteLine($"[GetNewSchedule] Day {day}.  best crop: {crop?.name}, numToPlant: {numToPlant}, availableTiles: {availableTiles}, availableGold: {availableGold}");

                payDays.Remove(day);

                foreach (var nexty in nextPayDays)
                {
                    if (payDays.TryGetValue(nexty.Key, out double originalWealth))
                        payDays[nexty.Key] = originalWealth + nexty.Value;
                    else
                        payDays[nexty.Key] = nexty.Value;
                }
            }

            return true;
        }

        /// This method tells you the actual days to plant on for the last schedule computed. Based on when you get money from harvests.
        /// Returns an array of size numDays - true means you are scheduled to plant on that day.
        /// NOTE: this is a nonessential detail, since you can just buy the best crop of the day whenever you have money. 
        public void GetPlantingDays(out bool[] plantingDays, out bool[] harvestDays)
        {
            plantingDays = new bool[numDays + 1];
            harvestDays = new bool[numDays + 1];
            bool[] profitDays = new bool[numDays + 1];

            int day = 1;
            profitDays[1] = plantingDays[1] = true;

            while (day <= numDays && memo.schedule.GetCrop(day) != null)
            {
                // Skip days not previously computed to be profitable
                if (!profitDays[day])
                {
                    ++day;
                    continue;
                }

                Crop crop = memo.schedule.GetCrop(day);

                if (crop != null)
                {
                    // Make sure planting on this day gives us time to harvest
                    if (profitDays[day])
                    {
                        if (day + crop.timeToMaturity <= numDays)
                            plantingDays[day] = true;
                    }

                    // Traversal: note future profit days created by this crop
                    for (int harvestDay = day + crop.timeToMaturity; harvestDay <= numDays; harvestDay = harvestDay + crop.yieldRate)
                    {
                        harvestDays[harvestDay] = true;

                        if (harvestDay < numDays)
                            profitDays[harvestDay + 1] = true;
                    }
                }

                ++day;
            }
        }

        private Crop MostProfitableCrop(int day, int maxDays, List<Crop> crops, int availableTiles, double availableGold, out double profit, out int numToPlant, out SortedDictionary<int, double> nextPayDays)
        {
            return MostProfitableCropRecursive(day, maxDays, crops, availableGold, availableTiles, out profit, out numToPlant, out nextPayDays);
        }

        /// <summary>
        /// Returns the most profitable crop to plant on day X.
        /// </summary>
        private Crop MostProfitableCropRecursive(int day, int maxDays, List<Crop> crops, double availableGold, int availableTiles, out double bestProfit, out int bestNumToPlant, out SortedDictionary<int, double> bestNextPayDays)
        {
            bestProfit = 0;
            Crop bestCrop = null;
            bestNumToPlant = 0;
            bestNextPayDays = new SortedDictionary<int, double>();

            if (crops == null || crops.Count <= 0 || day >= maxDays || availableTiles <= 0 || availableGold <= 0)
                return null;

            foreach (var crop in crops)
            {
                double cropProfit = 0;

                int numHarvests = crop.NumHarvests(day, maxDays);
                int unitsCanAfford = ((int)(availableGold / crop.buyPrice));
                bool tileLimited = availableTiles < unitsCanAfford;
                int unitsToPlant = tileLimited ? availableTiles : unitsCanAfford;

                int nextPlantDay = 0;
                double curGold = availableGold;
                var nextPayDays = new SortedDictionary<int, double>();

                int curTiles = availableTiles;

                // Rather than calculating every day, you only need to make a decision on the next payday.
                // Because you use up either all your money or all your tiles (or both), and payday is when gold and optionally tiles are returned to you.
                // Note: We could use harvest day (tiles) and the +1 payday (gold), but it creates a simpler schedule just to wait for payday.
                //       It might take time to harvest and sell the crops and remove their remains anyway.
                if (numHarvests == 0)
                {
                    // Not enough time to plant this!
                }
                else if (crop.buyPrice > availableGold)
                {
                    // Not enough money to plant this!
                }
                else if (numHarvests == 1)
                {
                    // Temporary crop. Gold AND tiles are returned.
                    cropProfit = unitsToPlant * (crop.sellPrice - crop.buyPrice);
                    nextPlantDay = day + crop.timeToMaturity + 1;
                    curGold = availableGold + cropProfit;
                    
                    double futureProfit = 0;
                    if (nextPlantDay <= maxDays)
                    {
                        nextPayDays.Add(nextPlantDay, curGold);
                        MostProfitableCropRecursive(nextPlantDay, maxDays, crops, curGold, curTiles, out futureProfit, out _, out _);
                    }
                    else
                        futureProfit = 0;

                    cropProfit += futureProfit;
                }
                else
                {
                    // Perma crop. Only gold is returned.
                    cropProfit = unitsToPlant * (crop.sellPrice - crop.buyPrice);
                    int iterNextDay = nextPlantDay = day + crop.timeToMaturity + 1;
                    double iterCurGold = curGold = availableGold + cropProfit;
                    int iterCurTiles = curTiles = availableTiles - unitsToPlant;

                    int iterUnitsToPlant = 0;
                    SortedDictionary<int, double> iterPayDays = null;
                    double futureProfit = 0;
                    if (nextPlantDay <= maxDays)
                    {
                        nextPayDays.Add(nextPlantDay, curGold);
                        MostProfitableCropRecursive(nextPlantDay, maxDays, crops, curGold, curTiles, out futureProfit, out iterUnitsToPlant, out  iterPayDays);
                    }
                    else
                        futureProfit = 0;

                    // Cumulative profit, not used for iteration
                    cropProfit += futureProfit;

                    for (int i = 0; i < numHarvests - 1; ++i)
                    {
                        iterNextDay += crop.yieldRate;

                        if (iterNextDay <= maxDays)
                        {
                            // TODO: we don't know the actually know the curGold and curTiles value on the second harvest day...

                            // The only gold we have on second harvest is from the second harvest. That's because the gold from the first harvest was presumably spent on some optimal crop.
                            iterCurGold = unitsToPlant * crop.sellPrice;
                            cropProfit += iterCurGold;
                            iterCurTiles = iterCurTiles - iterUnitsToPlant;
                            nextPayDays.Add(iterNextDay, iterCurGold);

                            // FutureProfit is the CUMULATIVE end-all profit that can be made by planting this thing.
                            var newCrop = MostProfitableCropRecursive(iterNextDay, maxDays, crops, iterCurGold, iterCurTiles, out futureProfit, out iterUnitsToPlant, out _);

                            cropProfit += futureProfit;
                        }
                    }
                }

                if (cropProfit > bestProfit)
                {
                    bestCrop = crop;
                    bestProfit = cropProfit;
                    bestNumToPlant = unitsToPlant;
                    bestNextPayDays = nextPayDays;
                }
            }

            return bestCrop;
        }
    }

    /// <summary>
    /// A single instance of a plant in the flat profit calculator simulation.
    /// </summary>
    public class Plant
    {
        public Crop Crop;
        public List<int> HarvestDays;
        public bool Persistent => HarvestDays.Count > 1;

        public Plant(Crop crop, int dayItWasPlanted, int maxDays, int daysToMaturity, int daysBetweenHarvests)
        {
            Crop = crop;

            HarvestDays = new List<int>();

            int harvestDate = dayItWasPlanted + daysToMaturity;
            HarvestDays.Add(harvestDate);

            while ((harvestDate + daysBetweenHarvests) <= maxDays)
            {
                harvestDate += daysBetweenHarvests;
                HarvestDays.Add(harvestDate);
            }
        }

        public override string ToString()
        {
            //return $"{Crop.name}: blooms on {string.Join(", ", HarvestDays.ToArray())}, persistent: {Persistent}";
            return $"{Crop.name}";
        }
    }

    public class DayDetails
    {
        // list of crops and their details
        public List<int> cropsNumberToPlant = new List<int>();

        public int totalNumberToPlant
        {
            get
            {
                if (cropsNumberToPlant == null)
                    return 0;

                int total = 0;
                foreach (var numberToPlant in cropsNumberToPlant)
                    total += numberToPlant;

                return total;
            }
        }

        public string ToString(List<Crop> cropList)
        {
            string summary = "";

            if (cropsNumberToPlant.Count == 0)
                return summary;
            else if (cropsNumberToPlant.Count != cropList.Count)
            {
                Console.WriteLine($"[DayDetails] ERROR: input cropList had {cropList.Count} crops, but this DayDetails has {cropsNumberToPlant.Count}.");
                return summary;
            }

            for (int i = 0; i < cropList.Count; ++i)
                summary += $"{cropList[i].name}: {cropsNumberToPlant[i]}, ";

            return summary;
        }
    }
}
