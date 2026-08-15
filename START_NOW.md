# Start trading NOW (NEW-PSUPERVISOR)

## 1. Pull
```bash
git fetch origin NEW-PSUPERVISOR
git checkout NEW-PSUPERVISOR
git pull origin NEW-PSUPERVISOR
```

## 2. Live control file (required for ON/OFF + weekends)
Copy once:
```
mkdir C:\VertexShared
copy VertexShared\trading_control.json C:\VertexShared\trading_control.json
```

Contents must be:
```json
{
  "tradingEnabled": true,
  "blockWeekends": false
}
```

## 3. Build & run
- Build solution `VertexAutoTradeBinance8.sln` (Release)
- Start **Web** publish or project (hosts bot worker)
- Or start bot host that runs `TradingWorker`

## 4. Settings UI
- **Trading ON**
- **Block weekends = NO** (unchecked)
- Save → check `C:\VertexShared\trading_control.json`

## Defaults in this branch
- TradingEnabled = true
- BlockWeekends = false (you can trade Saturday/Sunday)
- Sessions London+NY still apply (06:00–21:00 UTC with early start)
- MaxOpenPositions = 4

If still OBS: check log for `weekend block` or `TradingEnabled=false` or `Session off` (outside London/NY hours).
