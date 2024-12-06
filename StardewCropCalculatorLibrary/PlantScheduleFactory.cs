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
    /// <summary>
    /// This scheduler algorithm is a mathematical ROI function. It's fast, but can't deal with limited tiles. It assumes all gold is invested and multipled.
    /// </summary>
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
            crops = crops.Where(c => c.IsEnabled).ToList();

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
    }
}
