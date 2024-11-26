using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

        /// <summary>
        /// Modify schedule if all tiles get used up. Only modifies the schedule starting on the day tiles are used up.
        /// </summary>
        /// <returns>The day on which all tiles were used up.</returns>
        public bool LimitSchedule(List<Crop> crops, int availableTiles, double availableGold, PlantSchedule outSchedule, bool[] outPlantingDays, DayDetails[] outScheduleDetails)
        {
            if (availableTiles <= 0 || crops == null || crops.Count <= 0 || outPlantingDays == null || outPlantingDays.Length <= 0)
                return false;

            if (availableGold == 0)
                availableGold = 1000000000;

            double IntialGold = availableGold;
            int InitialTiles = availableTiles;

            Console.WriteLine($"Initial gold: {availableGold}, initial tiles: {availableTiles}");

            bool tileConservationMode = false;
            int tileConservationModeDay = 0;
            int usedTiles = 0;
            var persistentPlants = new List<Plant>();

            // Determine if tile limit breached
            for (int day = 1; day <= outSchedule.MaxDays; ++day)
            {
                var curCrop = outSchedule.GetCrop(day);
                int unitsPlanted = 0;
                double moneySpent = 0;

                if (curCrop != null && outPlantingDays[day])
                {
                    unitsPlanted = (int)(availableGold / curCrop.buyPrice);
                    moneySpent = unitsPlanted * curCrop.buyPrice;
                }

                if (!tileConservationMode && usedTiles + unitsPlanted <= availableTiles)
                {
                    if (outPlantingDays[day])
                    {
                        // Buy Day
                        usedTiles += unitsPlanted;
                        availableGold -= moneySpent;

                        // Save recurring crop for next time
                        for (int j = 0; j < unitsPlanted; ++j)
                            persistentPlants.Add(new Plant(curCrop, day, outSchedule.MaxDays, curCrop.timeToMaturity, curCrop.yieldRate));

                        Console.WriteLine($"Plant Day {day}: {usedTiles}/{availableTiles} tile usage, {availableGold}g. Planted {unitsPlanted} {curCrop?.name}");
                    }
                    else
                    {
                        // Sell Day
                        double moneyReceived = 0;
                        int unitsSold = 0;
                        List<Plant> plantsToRemove = new List<Plant>();

                        foreach (var persistentPlant in persistentPlants)
                        {
                            if (persistentPlant.HarvestDays.Contains(day))
                            {
                                moneyReceived += persistentPlant.Crop.sellPrice;
                                ++unitsSold;
                            }

                            if (!persistentPlant.Persistent)
                                plantsToRemove.Add(persistentPlant);
                        }

                        foreach (var plantToRemove in plantsToRemove)
                        {
                            persistentPlants.Remove(plantToRemove);
                            --usedTiles;
                        }

                        if (moneyReceived > 0)
                        {
                            availableGold += moneyReceived;
                            Console.WriteLine($" Sell Day {day}: {usedTiles}/{availableTiles} tile usage, {availableGold}g. ");
                        }
                    }

                }
                else
                {
                    Console.WriteLine($"Switching to tile conservation mode!  Day {day}: {usedTiles}/{availableTiles} tile usage. Can't plant {unitsPlanted} {curCrop?.name}");
                    tileConservationMode = true;

                    if (tileConservationModeDay == 0)
                        tileConservationModeDay = day;

                    break;

                    //// Find crop with highest single ROI.
                    //var bestDayCrop = crops[0];
                    //var bestDayCropIM = crops[0].InvestmentMultiple(day, numDays);

                    //foreach (var crop in crops)
                    //{
                    //    var cropIM = crop.InvestmentMultiple(day, numDays);

                    //    if (cropIM > bestDayCropIM)
                    //    {
                    //        bestDayCrop = crop;
                    //        bestDayCropIM = cropIM;
                    //    }
                    //}

                    //outSchedule.SetCrop(day, bestDayCrop);
                    //outPlantingDays[day] = false;
                }
            }

            // If filled tiles, make new schedule. Use strategy max-profit-per-tile
            if (tileConservationMode)
            {
                Console.WriteLine($"\n\nNEW SCHEDULE: tiles {InitialTiles}, gold {IntialGold}\n");

                persistentPlants.Clear();
                availableGold = IntialGold;
                availableTiles = InitialTiles;

                for (int day = 1; day <= outSchedule.MaxDays; ++day)
                {
                    outPlantingDays[day] = false;

                    // INVESTMENT MULTIPLE DOESN'T WORK! Need flat profit of using a single crop on a tile for all 28 days.
                    // Hmm but i think this strategy only works for when the tiles have filled up, say on day 15.
                    // So should maybe use original strategy up until day 15, then switch to flat profit strategy.
                    Crop bestDayCrop = null;
                    double bestDayCropProfit = 0;

                    foreach (var crop in crops)
                    {
                        int numHarvests = crop.NumHarvests(day, outSchedule.MaxDays);
                        double cropProfit = 0;

                        if (numHarvests == 0)
                        {
                            // Not enough time!
                        }
                        else if (numHarvests == 1)
                        {
                            int daysLeft = outSchedule.MaxDays - day;
                            numHarvests = daysLeft / crop.timeToMaturity;
                            cropProfit = crop.sellPrice * numHarvests - crop.buyPrice * numHarvests;
                        }
                        else
                        {
                            cropProfit = crop.sellPrice * numHarvests - crop.buyPrice;
                        }

                        if (cropProfit > bestDayCropProfit)
                        {
                            bestDayCrop = crop;
                            bestDayCropProfit = cropProfit;
                        }
                    }

                    outSchedule.SetCrop(day, bestDayCrop);

                    if (bestDayCrop != null)
                    {
                        Console.WriteLine($"Day {day}: most profitable plant: {bestDayCrop.name}, numHarvests: {bestDayCrop.NumHarvests(day, outSchedule.MaxDays)}, profit: {bestDayCropProfit}g");

                        // Selling Day
                        double moneyReceived = 0;
                        int unitsSold = 0;
                        List<Plant> plantsToRemove = new List<Plant>();
                        foreach (var persistentPlant in persistentPlants)
                        {
                            if (persistentPlant.HarvestDays.Contains(day))
                            {
                                moneyReceived += persistentPlant.Crop.sellPrice;
                                ++unitsSold;

                                if (!persistentPlant.Persistent)
                                    plantsToRemove.Add(persistentPlant);
                            }
                        }

                        foreach (var plantToRemove in plantsToRemove)
                        {
                            persistentPlants.Remove(plantToRemove);
                            ++availableTiles;
                            Console.WriteLine($"Remove harvested plant: {plantToRemove.Crop.name} Free tiles increased to {availableTiles}");
                        }

                        if (moneyReceived > 0)
                        {
                            availableGold += moneyReceived;
                            Console.WriteLine($"SELL DAY {day}: Sold {unitsSold} plants for {moneyReceived}g. Wallet: {availableGold}g");
                        }

                        // TODO: support input gold set to 0 to indicate unlimited

                        // Planting Day (available tiles)
                        if (availableTiles > 0 && availableGold >= bestDayCrop.buyPrice)
                        {
                            int unitsPlanted = 0;

                            // tile-limited buy
                            if (((int)(availableGold / bestDayCrop.buyPrice)) > availableTiles)
                            {
                                bool tileLimitedDay = true;
                                unitsPlanted = availableTiles;
                                var moneySpent = unitsPlanted * bestDayCrop.buyPrice;

                                availableGold -= moneySpent;
                                availableTiles = 0;

                                Console.WriteLine($"BUY DAY {day}: Planted {unitsPlanted} {bestDayCrop.name} for {moneySpent}g. Wallet: {availableGold}g, tile-limited day: {tileLimitedDay}");
                            }
                            // gold-limited buy
                            else
                            {
                                var tileLimitedDay = false;
                                unitsPlanted = (int)(availableGold / bestDayCrop.buyPrice);
                                var moneySpent = unitsPlanted * bestDayCrop.buyPrice;

                                availableGold -= moneySpent; // not 0 because remainder
                                availableTiles -= unitsPlanted;

                                Console.WriteLine($"BUY DAY {day}: Planted {unitsPlanted} {bestDayCrop.name} for {moneySpent}g. Wallet: {availableGold}g, tile-limited day: {tileLimitedDay}");
                            }

                            // Save recurring crop for next time
                            for (int j = 0; j < unitsPlanted; ++j)
                                persistentPlants.Add(new Plant(bestDayCrop, day, outSchedule.MaxDays, bestDayCrop.timeToMaturity, bestDayCrop.yieldRate));

                            outPlantingDays[day] = true;
                            outScheduleDetails[day] = new DayDetails() { numberToPlant = unitsPlanted };

                            Console.WriteLine($"outPlantingDay[{day}] = true");
                        }
                        else
                        {
                            outPlantingDays[day] = false;
                            outScheduleDetails[day] = new DayDetails() { numberToPlant = 0 };
                        }
                    }
                    else
                    {
                        outPlantingDays[day] = false;
                        outScheduleDetails[day] = new DayDetails() { numberToPlant = 0 };
                    }
                }
            }

            return tileConservationMode;
        }

        /// This method tells you the actual days to plant on for the last schedule computed. Based on when you get money from harvests.
        /// Returns an array of size numDays - true means you are scheduled to plant on that day.
        /// NOTE: this is a nonessential detail, since you can just buy the best crop of the day whenever you have money. 
        public bool[] GetPlantingDays()
        {
            bool[] plantingDays = new bool[numDays + 1];
            plantingDays = plantingDays.Select(x => false).ToArray();

            int day = 1;
            plantingDays[1] = true;

            while (day <= numDays && memo.schedule.GetCrop(day) != null)
            {
                if (!plantingDays[day])
                {
                    ++day;
                    continue;
                }

                Crop crop = memo.schedule.GetCrop(day);

                for (int harvestDay = day + crop.timeToMaturity; harvestDay <= numDays - 1 /* For subsequent plant day */; harvestDay = harvestDay + crop.yieldRate)
                {
                    var profitDay = harvestDay + 1;

                    // A profit day is only a planting day if there's time to mature
                    if (profitDay + crop.timeToMaturity <= numDays)
                        plantingDays[profitDay] = true;
                }

                ++day;
            }

            return plantingDays;
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
            return $"{Crop.name}: blooms on {string.Join(", ", HarvestDays.ToArray())}, persistent: {Persistent}";
        }
    }

    public class DayDetails
    {
        public int numberToPlant;
    }
}
