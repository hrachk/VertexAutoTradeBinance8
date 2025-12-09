using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Strategy;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Services
{
    public class EngineStateBuilder
    {
        private readonly StrategyEngine _strategy;
        private readonly SmartRegimeService _regime;
        private readonly LiquidityGuardService _liq;
        private readonly AiSelfLearningService _learn;
        private readonly RiskManager _risk;

        public EngineStateBuilder(
            StrategyEngine strategy,
            SmartRegimeService regime,
            LiquidityGuardService liq,
            AiSelfLearningService learn,
            RiskManager risk)
        {
            _strategy = strategy;
            _regime = regime;
            _liq = liq;
            _learn = learn;
            _risk = risk;
        }

        public EngineState Build(string symbol, string timeframe)
        {
            return new EngineState
            {
                Status = "Running",

                Mode = _strategy.CurrentMode ?? "Detecting",
                BalanceUsdt = _risk.LastBalanceUsdt,       // У тебя есть LastKnownBalance в RiskManager

                Symbol = symbol,
                Timeframe = timeframe,

                MarketRegime = _regime.LastBaseRegime.ToString(),
                SmartRegime = _regime.LastSmartRegime.ToString(),
                Slope = _regime.LastSlope,
                Volatility = _regime.LastVolatility,
                Confidence = _regime.LastConfidence,

                LiquidityDanger = _liq.LastDanger?.Block ?? false,
                LiquidityReason = _liq.LastDanger?.Reason.ToString() ?? "",

                SoftEntry = _strategy.LastSoftEntry,
                BlockedByLiquidity = _strategy.LastBlockedByLiquidity,

                LastUpdate = DateTime.UtcNow
            };
        }
    }
}
