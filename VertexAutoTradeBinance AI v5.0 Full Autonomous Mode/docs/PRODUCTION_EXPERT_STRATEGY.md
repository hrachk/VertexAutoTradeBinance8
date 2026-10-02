# Vertex AutoTrade — Production Expert Strategy (v2.0)

## Honest scope

No retail bot is "smarter than everyone." Edge is **discipline + filters + risk**, not prediction magic.
This build encodes institutional *process*: trade pullbacks in trends, fade only in ranges, never chase impulse, size by 1R with equity notional cap.

## Architecture

```
Market data (15m+)
    → StrategyCore (TREND leg): Pullback → BreakoutRetest(majors) only
    → MeanReversion (RANGE leg): Z-score extremes, non-majors, ATR not expanding
    → StrategyRouter (DualMode regime TREND/RANGE/CHAOS)
    → TradingWorker: executable policy + DualMode flow/volume + Risk 1R
    → Live OrderExecutor + ApprovedEntry → Demo (same decision)
```

## TREND leg (CORE)

| Rule | Why |
|------|-----|
| **Pullback-first** | Institutions reload at value; chasing structure highs is retail FOMO |
| **No SimpleTrend** | Was primary late-entry path after local slope |
| **Breakout only + retest, majors** | Naked breakouts are liquidity events |
| **HTF bias (EMA50 stack)** | Do not long under falling slow EMA |
| **RSI FOMO guard** | Block long RSI>58 / short RSI<42 |
| **Impulse chase ban** | No entry after 1.35 ATR impulse close at extreme |
| **Volume ≥ 1.05× avg** | Capital participation required |
| **R:R TP1 ≥ 1.5** | Math of expectancy |
| **Structure SL + min 0.8%** | Avoid micro-stops and lottery stops |
| **Max notional 28% equity** | One name cannot dominate account |

## RANGE leg (MEANREV → RANGE_SPREAD_*)

| Rule | Why |
|------|-----|
| Only when DualMode **RANGE** | Z-score fails in trends (literature: high WR, low expectancy if fadetrend) |
| No BTC/ETH/BNB/SOL/XRP | Majors trend more; spread edge on mid-alts |
| **ATR not expanding** | Expanding ATR = trend ignition — do not fade |
| Volume surge + reversal bar | Confirmation, not knife-catch |
| Leverage 3–5×, small size | DualMode RangeSizeMult |

## Risk

- 1R budget ~0.6–0.75% equity (hard USD cap)
- MaxOpenPositions from UI
- Profit lock STEP1/STEP2 after TP path
- Kill / Daily DD / BTC vol filters remain

## Ops checklist

1. `git pull` → rebuild Engine + Web
2. Demo 30–50 trades before raising Live size
3. History **Setup** column: `TREND: CORE_*` vs `RANGE: RANGE_SPREAD_*`
4. Logs: `[CORE] REJECT htf_bias|impulse_chase|no_volume_flow`, `[DUAL-MODE]`, `[RISK] notional-cap`

## What this does *not* claim

- No guaranteed win rate
- No news/orderflow alpha without quality data feeds
- ML skip gate remains optional until shadow ΔPF proven
