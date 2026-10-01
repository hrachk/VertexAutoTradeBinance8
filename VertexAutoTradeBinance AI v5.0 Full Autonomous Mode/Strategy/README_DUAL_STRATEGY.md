# Dual Strategy (replaces CORE-only)

## Legs

| Regime (BTC DualMode) | Strategy | Reasons | Universe / risk |
|----------------------|----------|---------|-----------------|
| **TREND** | Structure / pullback / breakout (`StrategyCoreEngine`) | `CORE_*` | Top liquid + capital flow; lev 5–10x |
| **RANGE** | Z-score mean-reversion / spread (`MeanReversionEngine`) | `RANGE_SPREAD_*` | Non-majors only; lev 3–5x, small size |
| **CHAOS** | None | — | No new entries |

## Router

`StrategyRouter` forwards:
- CORE → channel only when regime ≠ RANGE/CHAOS
- MeanRev → channel only when regime ≠ TREND/CHAOS, and symbol is not BTC/ETH/BNB/SOL/XRP

## Execution filter

`ExecutableStrategyPolicy.IsLiveExecutable` allows `CORE_`, `TREND_`, `RANGE_`, `MEANREV_` for Live and Demo (same path).
