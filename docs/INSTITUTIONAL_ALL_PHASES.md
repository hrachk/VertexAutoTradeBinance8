# Institutional Pipeline — Phases 1–5

## Layout

```
src/VertexAutoTrade.Core/
src/VertexAutoTrade.RiskEngine/     # Phase 1 + InstitutionalEntryPipeline
src/VertexAutoTrade.Regime/         # Phase 2
src/VertexAutoTrade.Execution/      # Phase 3 OI/Funding + SmartOrderPolicy
src/VertexAutoTrade.NewsMacro/      # Phase 4
src/VertexAutoTrade.MLEngine/       # Phase 5
tests/VertexAutoTrade.RiskEngine.Tests/
```

## Gate order (before order)

1. HardRisk (daily R / equity / max positions / block)
2. AntiTilt (3× SL_STRATEGY_FAIL cool-off, weak avgR → risk×0.5)
3. Macro calendar blackout
4. News sentiment (shadow size× by default)
5. Market regime (block HighVolatilityChop)
6. BTC spike pause for alts
7. OI + Funding extremes
8. ML P(win) — shadow skip / probe 0.25×
9. PositionSizer (no SL widen)

## Worker integration

`TradingWorker` calls `InstitutionalEntryPipeline.Evaluate` before `GetPropDeskQtyFinal`.
Rejects with stage `INST_{Layer}`. Multiplies `signal.SizeMultiplier`.

## Config (appsettings)

```json
"RiskEngine": {
  "Hard": { "MaxDailyLossR": 3, "MaxOpenPositions": 3, "BaseRiskFraction": 0.01 },
  "AntiTilt": { "ConsecutiveStrategyFailsToCoolOff": 3, "CoolOffHours": 4 }
}
```

Wire options later via `services.Configure<HardRiskOptions>(...)`.

## Enable ML hard reject

Construct `MlSetupClassifier(hardReject: true)` after shadow KPI proves edge.

## Tests

```bash
dotnet test tests/VertexAutoTrade.RiskEngine.Tests
```
