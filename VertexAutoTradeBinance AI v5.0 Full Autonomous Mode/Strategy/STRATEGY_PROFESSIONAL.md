# Institutional strategy (rebuild from zero)

## What was deleted from the live path
- 15m-only pullback / structure chase (`TryPullback` as emit source)
- SimpleTrend continuation
- Breakout-retest as primary emit
- HTF-anchored “v3” hybrid that still entered parabolic alts

Legacy methods may remain in `StrategyCoreEngine.cs` for reference but **EvaluateAsync does not call them**.

## Live TREND path (only)
`InstitutionalTrendSetup.TryBuild` → `StrategyCoreEngine.EvaluateAsync` → Router → Worker → Sanity/DualMode/Risk → order

| Rule | Detail |
|------|--------|
| Bias | 1H HH/HL or LH/LL + EMA21/50 |
| Location | LONG only **discount** (≤ equilibrium of last 1H swing); SHORT only **premium** |
| Trigger | 15m reject at EMA21 in bias direction |
| SL | 1H swing invalidation (± pad), risk capped 0.7–3.2% |
| TP | 1.5R / 2.5R / 4.0R |
| Ban | Parabolic top/bot, vertical 12h without retrace, 15m impulse window, dead 1H volume, BTC against alts |
| Reason codes | `CORE_INST_LONG` / `CORE_INST_SHORT` |

## Live RANGE path
Unchanged channel: `MeanReversionEngine` → `RANGE_SPREAD_*` when DualMode = Range (non-majors, low leverage). Still passes `EntrySanityGate`.

## Not “edge”
Sizing, DualMode universe, journal, Demo parity — infrastructure only. Edge is only the rules above.
