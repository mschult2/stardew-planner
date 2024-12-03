using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StardewCropCalculatorLibrary
{
    public class Crop
    {
        public int timeToMaturity { get; set; }
        public int yieldRate { get; set; }
        public string name { get; set; }

        public double buyPrice { get; set; }
        public double sellPrice { get; set; }

        /// <summary> Per-tile profitability index. How much money this crop makes occupying one tile over the course of the month. </summary>
        public int TPI
        {
            get
            {

                if (GameStateCalendarFactory.IsPersistent(this))
                {
                    var numHarvests = NumHarvests(1, 28);
                    return (int)((numHarvests * sellPrice) - buyPrice);
                }
                else
                {
                    int numHarvests = 28 / (timeToMaturity + 1);
                    return (int)((numHarvests * sellPrice) - (numHarvests * buyPrice));
                }
            }
        }

        public int CurrentProfitIndex(int day)
        {
            int numHarvestsLeft = NumHarvests(day, 28);

            if (GameStateCalendarFactory.IsPersistent(this))
                return (int)((numHarvestsLeft * sellPrice) - buyPrice);
            else
                return (int)((NumHarvests(day, 28) * sellPrice) - numHarvestsLeft * buyPrice);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="timeToMaturity">time to crop's first yield</param>
        /// <param name="yieldRate">days between succesive yields after maturity</param>
        /// <param name="buyPrice">price that the seed was bought for</param>
        /// <param name="sellPrice">price that the crop will be sold at</param>
        public Crop(string name, int timeToMaturity, int yieldRate, double buyPrice, double sellPrice)
        {
            this.name = name;
            this.timeToMaturity = timeToMaturity;
            this.yieldRate = yieldRate;
            this.buyPrice = buyPrice;
            this.sellPrice = sellPrice;
        }

        // If something costs $1 and sells for $3, its "multiple of money" is 3.  I.e., your money triples.
        public double InvestmentMultiple(int day, int maxDays)
        {
            return Return(day, maxDays) + 1;
        }

        /// <summary>
        /// The measure of PROFIT when harvesting this plant over the course of the year (as a multiple of the original cost).
        /// So a return of 2.0 means your profit is double the cost, which means you tripled your money. A return of 0 means no profit was made (and no money lost).
        /// A return of -1 means all money was lost.
        /// </summary>
        /// <param name="day">day crop is planted</param>
        /// <param name="maxDays">days in the season</param>
        /// <returns></returns>
        public double Return(int day, int maxDays)
        {
            return ((NumHarvests(day, maxDays) * sellPrice) - buyPrice) / buyPrice;
        }

        /// <summary>
        /// The number of harvests you get out of planting a crop on a given day.
        /// </summary>
        /// <param name="dayPlanted">day crop is planted</param>
        /// <param name="maxDays">days in the season</param>
        /// <returns></returns>
        public int NumHarvests(int dayPlanted, int maxDays)
        {
            if (dayPlanted < 1 || maxDays < 1 || dayPlanted > maxDays)
                throw new Exception("dayPlanted and maxDays must be greater than 0, and dayPlanted must be less than or equal to maxDays.");

            int numHarvests = 0;

            if (yieldRate == -1)
                numHarvests = (maxDays - dayPlanted - timeToMaturity >= 0) ? 1 : 0;
           else
                numHarvests = (int)((maxDays - dayPlanted - timeToMaturity + yieldRate) / yieldRate); // rounds to floor

            return numHarvests < 0 ? 0 : numHarvests;
        }

        public override string ToString()
        {
            return name;
        }
    }
}

