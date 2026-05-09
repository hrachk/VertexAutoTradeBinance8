///


/*
 * Логика bias (очень простая, но рабочая)
1D StrongUpTrend  → LONG ONLY
1D StrongDownTrend → SHORT ONLY

4H против 1D → Neutral (осторожно)
1H совпадает с 1D → ConfidenceBoost


❗ Никаких входов на HTF
❗ Никаких SL/TP на HTF

Только разрешение / запрет стороны.
  */

namespace VertexAutoTradeBinance8.Models.HTF
{
    using VertexAutoTradeBinance8.Models;

    
    public sealed class MarketContextSnapshot
    {
        public MarketRegime Regime1H { get; init; }
        public MarketRegime Regime4H { get; init; }
        public MarketRegime Regime1D { get; init; }

        public MarketBias Bias { get; init; }                 // LongOnly / ShortOnly / Neutral
        public decimal ConfidenceBoost { get; init; }         // 1.0–1.25
        public decimal RiskBias { get; init; } = 1.0m;        // 0.8–1.0

        public bool MultiTFAligned { get; init; }
        public bool HighVolatilityContext { get; init; }
        public bool ManipulationRisk { get; init; }

        //public bool Allows(SignalSide side) =>
        //    Bias switch
        //    {
        //        MarketBias.LongOnly => side == SignalSide.Buy,
        //        MarketBias.ShortOnly => side == SignalSide.Sell,
        //        _ => true
        //    };

        public bool Allows(SignalSide side, decimal confidence)
        {
            if (Bias == MarketBias.LongOnly && side == SignalSide.Sell)
                return confidence >= 0.85m;

            if (Bias == MarketBias.ShortOnly && side == SignalSide.Buy)
                return confidence >= 0.85m;

            return true;
        }
    }

    public enum MarketBias
    {
        Neutral,
        LongOnly,
        ShortOnly
    }


}
