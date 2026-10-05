# Institutional TREND v2 — professional geometry

## Hierarchy
| TF | Role |
|----|------|
| **4H** | Permission: long blocked if price under 4H EMA50 / bearish structure |
| **1H** | Bias (HH/HL) + swing invalidation + ATR for stop width |
| **15m** | Trigger only (reject into EMA21) |

## Stop-Loss (not noise)
```
slPad = max(0.35 * ATR(1H), 0.90 * ATR(15m))
SL long  = min(swingLow - pad, entry - 0.9%)  then clamp risk ≤ 2.8%
SL short = max(swingHigh + pad, entry + 0.9%) then clamp risk ≤ 2.8%
```
- **Min risk distance 0.9%** — kills micro-stops (~0.2%) that always stop out.
- **Max 2.8%** — keeps 1R sizing sane.
- TP ladder: **1.6R / 2.8R / 4.5R**.

## Location
- Long only if price in **lower 42%** of last 1H swing range.
- Short only in **upper 42%**.

## Reasons
`CORE_INST_LONG` / `CORE_INST_SHORT`  
Rejects: `h4_blocks_long`, `long_not_in_discount`, `parabolic_top`, …
