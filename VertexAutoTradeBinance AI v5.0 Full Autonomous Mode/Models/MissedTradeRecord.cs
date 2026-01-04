using System;
using System.Collections.Generic;

namespace VertexAutoTradeBinance8.Models
{
    public class MissedTradeRecord
    {
        public string Symbol { get; set; } = string.Empty;
        public DateTime Time { get; set; }

        public decimal Entry { get; set; }
        public decimal StopLoss { get; set; }
        public string Side { get; set; } = string.Empty;

        public List<decimal> TakeProfits { get; set; } = new();

        public string Reason { get; set; } = string.Empty;

        public decimal FreeBalance { get; set; }
        public decimal AttemptNotional { get; set; }
        public decimal RequiredMinNotional { get; set; }

        public decimal Atr { get; set; }
        public decimal Slope { get; set; }
        public decimal Vol { get; set; }
        public decimal Deviation { get; set; }
        public MarketRegime Regime { get; set; } 

        public int Confidence { get; set; }
        public string SmartType { get; set; } = string.Empty;
        public int Score { get; set; }
    }
}
