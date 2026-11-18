# Vertex backtest — CORE trend setup + R-ladder

Replays the engine's own `InstitutionalTrendSetup` and `EntrySanityGate` (the source files are
linked, not copied) on real Binance USDⓈ-M klines, then walks every trade's exit with the same
R-ladder (`RLadderPolicy`) that Demo and Live use. Use it to choose ladder settings from data.

## Run

```powershell
# from the repo root (needs internet to fapi.binance.com, no API key)
dotnet run -c Release --project tools/VertexAutoTrade.Backtest -- --days 120
```

Options:

| Option | Default | Meaning |
|---|---|---|
| `--symbols` | 12 liquid USDT perps | Comma list, e.g. `BTCUSDT,ETHUSDT,SOLUSDT` |
| `--days` | 120 | Test window (plus ~100 days of warm-up history) |
| `--exec` | `5m` | Bar size used to walk exits (`1m` is slower and more exact) |
| `--risk` | `0.0075` | Risk per trade for the compounded equity column |
| `--out` | `./bt-results` | Output folder |

Klines are cached in `./bt-data`, so a rerun with the same window is instant.

## Output

* `bt-results/summary.md` — one row per configuration: signals, trades, win %, average win/loss
  in R, expectancy, profit factor, sum of R, max drawdown in R, compounded equity multiple.
* An **MFE table**: the share of trades whose best excursion reached 1R, 1.5R, 2R, 3R, 4R, 5R, 7R.
  These are the numbers to type into the Risk desk calculator.
* `trades_<config>.csv` — every trade (entry, stop, R, peak R, targets hit, exit type).

Configurations compared on the same data:

1. Legacy exits (TP1 ≈ 1.15R, 33/33/34, step-lock at +0.5R / +0.85R)
2. Ladder with the old 1H-structure stop, no room filter
3. Ladder with the pullback stop, no room filter
4. Ladder with the pullback stop, room ≥ 2R (**default**)
5. Ladder with the pullback stop, room ≥ 3R
6. Ladder with the structure stop, room ≥ 2R

## Honest limits

* It models the CORE setup and sanity gate only. The worker's later gates (signal auction,
  SmartFlow order book, news, sessions, correlation limits, BTC bias) are not replayed, so live
  trade count will be lower than here.
* Fills: next 15m open + 2 bps slippage, 0.05% taker fee per side, stop assumed before target
  when one bar touches both. Funding is not charged.
* Fewer than ~100 trades says nothing. Split the window (e.g. two halves) and keep a setting only
  if it wins in both halves.
