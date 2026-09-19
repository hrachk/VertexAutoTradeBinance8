# Adaptive Memory & ML Gate — Phases 1–3

## Phase 1 — Contextual SL (`SlAttribution`)
Codes: SL_STRATEGY_FAIL, SL_MARKET_CORRELATION, SL_NEWS_SPIKE, SL_WHIPSAW, SL_BE_HIT
Weights: strategy 1.0, correlation 0.40, news 0.35, whipsaw 0.50, BE 0.70
consecutiveStops only on full-weight strategy stops.

## Phase 2 — Shadow ML (`ShadowMlGatekeeper`)
Logs [ML-SHADOW]; optional SharedData/ml_skip_model.json
EnableMlSkipGate default false.

## Phase 3 — Time-decay & Probe
HalfLifeDays default 7; SoftSkip + high conf → probe size 0.25.
