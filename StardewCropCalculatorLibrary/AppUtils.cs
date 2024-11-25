using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StardewCropCalculatorLibrary
{
    /// <summary>
    /// Static utility functions for this Stardrew application.
    /// </summary>
    static class AppUtils
    {
        // Our application's fail policy
        public static void Fail(string errorMsg)
        {
            System.Diagnostics.Debug.WriteLine(errorMsg);
            throw new Exception(errorMsg);
        }
    }
}
