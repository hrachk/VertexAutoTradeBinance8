# News Filter Alpha Impact

## Changes
- Severity matrix: A Macro HARD_BLOCK, B Token size 0.25-0.5, C Infrastructure micro
- Dynamic windows: lead = k1 * ATR_ratio; RegisterShadowBlock for KPI
- Microstructure: spread/depth reason codes
- Log: SharedData/news_shadow_decisions.jsonl
- RecommendEnableGate: n>=50, dPF>=0.15, Filtered DD <= Actual DD

## Config
News:ShadowMode=true (default). Set false only after green gate metrics.
