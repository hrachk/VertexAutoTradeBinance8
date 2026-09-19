#!/usr/bin/env python3
"""
Train a lightweight skip-gate model from trade-features.jsonl
and export SharedData/ml_skip_model.json for ShadowMlGatekeeper.

Usage:
  python scripts/train_ml_skip_gate.py --input path/to/trade-features.jsonl --output path/to/ml_skip_model.json
  python scripts/train_ml_skip_gate.py --input ./client_001/trade-features.jsonl --output ./ml_skip_model.json --threshold 0.42

Requires: pip install scikit-learn numpy
Optional: pip install lightgbm
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path


FEATURE_KEYS = [
    "conf",
    "sizeMult",
    "softSkip",
    "recentStops",
    "recentWins",
    "slPad",
    "isCore",
    "realizedR",  # training target helper only when present in offline rows
]


def load_rows(path: Path) -> list[dict]:
    rows = []
    with path.open(encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return rows


def row_features(r: dict) -> dict[str, float]:
    conf = float(r.get("SignalConf") or r.get("conf") or 0)
    if conf > 1.5:
        conf /= 100.0
    reason = str(r.get("CloseReason") or "")
    is_sl = "SL" in reason.upper() or float(r.get("RealizedPnl") or 0) < 0
    return {
        "conf": conf,
        "sizeMult": float(r.get("sizeMult") or 1.0),
        "softSkip": 1.0 if r.get("softSkip") else 0.0,
        "recentStops": float(r.get("recentStops") or 0),
        "recentWins": float(r.get("recentWins") or 0),
        "slPad": float(r.get("slPad") or 0),
        "isCore": 1.0 if str(r.get("CloseReason") or "").startswith("CORE") or r.get("isCore") else 0.0,
        "label_win": 0.0 if is_sl else 1.0,
        "realizedR": float(r.get("RealizedR") or 0),
    }


def train_logistic(X, y):
    try:
        from sklearn.linear_model import LogisticRegression
        import numpy as np
        clf = LogisticRegression(max_iter=500, class_weight="balanced")
        clf.fit(X, y)
        weights = {FEATURE_KEYS[i]: float(clf.coef_[0][i]) for i in range(len(FEATURE_KEYS))}
        bias = float(clf.intercept_[0])
        return bias, weights, "sklearn-logistic"
    except Exception as e:
        print("sklearn unavailable or failed:", e, file=sys.stderr)
        # Manual mean difference heuristic
        wins = [X[i] for i in range(len(y)) if y[i] == 1]
        losses = [X[i] for i in range(len(y)) if y[i] == 0]
        if not wins or not losses:
            return 0.0, {k: 0.0 for k in FEATURE_KEYS}, "empty"
        import numpy as np
        wmean = np.mean(wins, axis=0)
        lmean = np.mean(losses, axis=0)
        delta = wmean - lmean
        weights = {FEATURE_KEYS[i]: float(delta[i]) for i in range(len(FEATURE_KEYS))}
        bias = 0.0
        return bias, weights, "mean-diff-heuristic"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", required=True, help="trade-features.jsonl path")
    ap.add_argument("--output", required=True, help="ml_skip_model.json path")
    ap.add_argument("--threshold", type=float, default=0.42)
    args = ap.parse_args()

    inp = Path(args.input)
    if not inp.exists():
        print(f"Input not found: {inp}", file=sys.stderr)
        sys.exit(1)

    rows = load_rows(inp)
    feats = [row_features(r) for r in rows]
    # need both classes
    y = [int(f["label_win"]) for f in feats]
    if len(feats) < 20:
        print(f"WARNING: only {len(feats)} rows — model will be weak", file=sys.stderr)
    if sum(y) == 0 or sum(y) == len(y):
        print("Need both wins and losses in features file", file=sys.stderr)
        sys.exit(2)

    X = [[f[k] for k in FEATURE_KEYS] for f in feats]
    bias, weights, src = train_logistic(X, y)

    model = {
        "bias": bias,
        "weights": weights,
        "threshold": args.threshold,
        "feature_order": FEATURE_KEYS,
        "trained_on_rows": len(feats),
        "source": src,
    }
    out = Path(args.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(model, indent=2), encoding="utf-8")
    print(f"Wrote {out} ({src}, n={len(feats)}, thr={args.threshold})")
    print("Copy to SharedData:Root/ml_skip_model.json and restart Engine.")


if __name__ == "__main__":
    main()
