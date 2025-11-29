using System;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Масштабирует риск по оценке AI: STRONG → 1.6x, GOOD → 1.2x и т.д.
    /// </summary>
    public class AiRiskScalerV2
    {
        public decimal Scale(string grade)
        {
            if (grade == null) return 1.0m;

            switch (grade.ToUpperInvariant())
            {
                case "STRONG": return 1.6m;
                case "GOOD": return 1.2m;
                case "OK": return 1.0m;
                case "BORDER": return 0.7m;
                case "BLOCK": return 0m;
                default: return 1.0m;
            }
        }
    }
}
