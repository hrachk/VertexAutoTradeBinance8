# Offline ML Skip Gate

## 1. Collect data
After Demo/Live trades, use:
`{SharedData:Root}/client_001/trade-features.jsonl`

## 2. Train
```bash
pip install scikit-learn numpy
python scripts/train_ml_skip_gate.py \
  --input /path/to/client_001/trade-features.jsonl \
  --output /path/to/SharedData/ml_skip_model.json \
  --threshold 0.42
```

## 3. Shadow validation
Keep `MlGate:EnableMlSkipGate = false`.
Watch logs `[ML-SHADOW]` and `[ML-KPI]`, Telegram `/status` ML line, file `ml_shadow_kpi.json`.

## 4. Production hard gate
Only after shadow metrics look better (higher WR / lower DD on filtered set):
```json
"MlGate": { "EnableMlSkipGate": true, "SkipThreshold": 0.42 }
```
