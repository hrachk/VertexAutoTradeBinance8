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

## StrategyCore v3.0 — HTF-anchored (not local 15m)

**Problem fixed:** entries were born from 15m EMA touch + 15m swing SL → noise stops (`SL_STRATEGY_FAIL`) while “structure” looked valid on the wrong TF.

**Model now:**
1. **1H** HH/HL or LH/LL + EMA21/50 → direction (where structure/capital sits).
2. **SL** = 1H swing invalidation (capped 3.5%), not 15m micro-swing.
3. **15m** only *times* the entry (pullback to fast EMA + reject candle in HTF direction).
4. **1H volume** participation required; dead 1H volume → no trade.
5. **Alts** soft-blocked if BTC 1H strongly opposes.
6. Reasons: `CORE_HTF_PB_LONG` / `CORE_HTF_PB_SHORT`.

RANGE leg unchanged (`RANGE_SPREAD_*` via MeanReversion when DualMode = Range).
