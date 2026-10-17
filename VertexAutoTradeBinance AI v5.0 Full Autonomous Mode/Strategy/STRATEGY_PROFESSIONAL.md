# Institutional TREND v3 — coherent pullback

## Why v2 emitted almost nothing

v2 required all of these at once:

- bullish structure (price making higher highs — so price is high in the swing)
- price in the **lower 42%** of that same swing
- price in the **top 28%** of the last 24×1H banned as "parabolic"
- stop clamped **inside** the swing when risk exceeded 2.8%
- stop floor in code was **0.55%** while the sanity gate required **0.9%**

An uptrend cannot be both at the highs (structure) and in discount, and a trend stop that is pulled closer than invalidation is stopped by noise. v3 removes that contradiction.

## Hierarchy

| TF | Role |
|----|------|
| **4H** | Hard-block **alts** only when EMA side and structure both disagree. Majors pay a confidence penalty. Closed 4H bar only (age ≥ 180m). |
| **1H** | Bias: EMA21/50 stack + defended higher-low (long) or lower-high (short). Swing = invalidation. |
| **15m** | Trigger: EMA21 touch inside the last 4 closed bars + rejection or reclaim. |

## Location

Retrace of the last 1H impulse, not a fixed discount box:

- long: (swingHigh - close) / (swingHigh - swingLow) in 15% .. 72%
- short: mirror
- chase (under 15%) and broken trend (over 72%) are rejected

## Stop

```
sl = swing invalidation ∓ max(0.35×ATR1H, 0.90×ATR15)
risk < 0.9%  → widen to 0.9% (away from noise)
risk > 2.8%  → no trade (do not pull the stop inside the swing)
```

RANGE leg uses **0.45% .. 2.2%** (mean-reversion). Same rule: never tighten inside the extreme.

## Take profit

Nearest opposing 15m liquidity, else 1.35R. Trade is dropped if TP1 R:R is under 1.35.

## Live price

The worker no longer overwrites `EntryPrice` with the ticker. The live price is accepted only when the stop stays on the correct side, risk stays inside the band, chase ≤ 0.35%, and TP1 R:R ≥ 1.15.

## Reasons

`CORE_INST_LONG` / `CORE_INST_SHORT`

Rejects: `no_clear_1h_bias`, `long_chase`, `long_too_deep`, `no_m15_long_trigger`, `sl_too_wide`, `rr_fail`, `h4_against_long`, `vertical_long`, `btc_against_long`.
