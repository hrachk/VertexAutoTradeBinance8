using System;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Scales position risk by AI signal grade.
    ///
    /// Conservative multipliers for high-leverage futures (19-25x):
    ///   At 3% base risk × 1.6 STRONG = 4.8% → with 4 positions = 19.2% exposure.
    ///   Reduced STRONG to 1.25 so max single-trade risk stays ≤ 3.75%
    ///   and total exposure with 4 positions stays ≤ 15%.
    ///
    /// Grade → multiplier mapping:
    ///   STRONG  1.25x  (was 1.6 — too aggressive at 19-25x leverage)
    ///   GOOD    1.10x  (was 1.2)
    ///   OK      1.00x  (neutral, unchanged)
    ///   BORDER  0.65x  (was 0.7 — slightly more conservative)
    ///   BLOCK   0.00x  (signal fully rejected, unchanged)
    /// </summary>
    public class AiRiskScalerV2
    {
        public decimal Scale(string grade)
        {
            if (grade == null) return 1.0m;

            switch (grade.ToUpperInvariant())
            {
                case "STRONG": return 1.25m;   // was 1.6 — capped for leverage safety
                case "GOOD":   return 1.10m;   // was 1.2
                case "OK":     return 1.00m;
                case "BORDER": return 0.65m;   // was 0.7
                case "BLOCK":  return 0m;
                default:       return 1.00m;
            }
        }
    }
}
