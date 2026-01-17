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
        private readonly EngineStateSnapshotService _stateSvc;


        public EngineStateBuilder(
            StrategyEngine strategy,
            SmartRegimeService regime,
            LiquidityGuardService liq,
            AiSelfLearningService learn,
            RiskManager risk,
            EngineStateSnapshotService stateSvc)
        {
            _strategy = strategy;
            _regime = regime;
            _liq = liq;
            _learn = learn;
            _risk = risk;
            _stateSvc = stateSvc;
        }

        public EngineState Build(string symbol, string timeframe)
        {
            var s = _stateSvc.State;

            var confidenceRaw = _regime.LastConfidence; // 0..1

            int confidencePercent = (int)Math.Round(confidenceRaw * 100m);

            string confidenceLevel =
                confidencePercent >= 75 ? "HIGH" :
                confidencePercent >= 45 ? "MEDIUM" :
                "LOW";

            return new EngineState
            {
                Status = "Running",

                Mode = _strategy.CurrentMode ?? "Detecting",
                BalanceUsdt = _risk.LastBalanceUsdt,

                Symbol = symbol,
                Timeframe = timeframe,

                MarketRegime = _regime.LastBaseRegime.ToString(),
                SmartRegime = _regime.LastSmartRegime.ToString(),
                Slope = _regime.LastSlope,
                Volatility = _regime.LastVolatility,

                // ✅ КАНОНИЧЕСКИ
                ConfidenceRaw = confidenceRaw,
                ConfidencePercent = confidencePercent,
                ConfidenceLevel = confidenceLevel,

                LiquidityDanger = _liq.LastDanger?.Block ?? false,
                LiquidityReason = _liq.LastDanger?.Reason.ToString() ?? "",

                SoftEntry = _strategy.LastSoftEntry,
                BlockedByLiquidity = _strategy.LastBlockedByLiquidity,
                SupervisorChecksLastMinute = s.SupervisorChecksLastMinute,
                LastSupervisorAction = s.LastSupervisorAction,
                LastSupervisorMessage = s.LastSupervisorMessage,
                LastUpdate = DateTime.UtcNow
            };

            
        }
    }
}
