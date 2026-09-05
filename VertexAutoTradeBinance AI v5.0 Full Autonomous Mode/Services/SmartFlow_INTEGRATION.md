# SmartFlowGuard — интеграция (не ломает CORE)

## Файлы скопировать в проект

1. `Configuration/SmartFlowOptions.cs`
2. `Services/SmartFlowGuardService.cs`

## 1) appsettings.json — секция

```json
"SmartFlow": {
  "Enabled": true,
  "AllowHardBlock": true,
  "EnableSlWiden": true,
  "UseClusterService": true,
  "MaxSpreadPct": 0.0008,
  "BlockSpreadPct": 0.0025,
  "SoftImbalance": 0.45,
  "HardImbalance": 0.72,
  "DeltaBars": 8,
  "SoftAdverseDelta": 0.58,
  "HardAdverseDelta": 0.72,
  "SoftSizeMult": 0.70,
  "FundingSizeMult": 0.75,
  "BlockOnFunding": true,
  "MinTopNotionalUsd": 2500,
  "Depth": 50
}
```

Отключить слой без удаления кода: `"Enabled": false`.

## 2) Program.cs — DI

Рядом с `LiquidityGuardService` / `FundingRateService`:

```csharp
services.Configure<VertexAutoTradeBinance8.Configuration.SmartFlowOptions>(
    ctx.Configuration.GetSection("SmartFlow"));
services.AddSingleton<SmartFlowGuardService>();
```

## 3) TradingWorker.cs

### Поле

```csharp
private readonly SmartFlowGuardService _smartFlow;
```

### ctor — параметр + присвоение

```csharp
SmartFlowGuardService smartFlow,
// ...
_smartFlow = smartFlow;
```

### После блока `// 5) LIQUIDITY GUARD` и **до** расчёта qty:

```csharp
// =====================================================
// 5.5) SMART FLOW GUARD (Live microstructure — additive)
// Structure signal already passed; this only blocks / cuts size
// or WIDENS SL. Fail-open. Does not change CORE generation.
// =====================================================
try
{
    IReadOnlyList<BinanceFuturesUsdtKline>? flowKlines = null;
    try
    {
        flowKlines = await _marketDataFacade
            .GetKlinesAsync(symbol, tf, 40, ct)
            .ConfigureAwait(false);
    }
    catch { /* soft */ }

    var flow = await _smartFlow.EvaluateAsync(signal, flowKlines, ct).ConfigureAwait(false);

    signal.LiquidityScore = flow.Score;
    signal.LiquidityDetails = flow.Details;

    if (flow.Block)
    {
        await RejectAsync(
            signal, symbol, tf,
            "SMARTFLOW",
            flow.Reason,
            ct,
            extra: flow.Details);
        return;
    }

    if (flow.SizeMult > 0m && flow.SizeMult < 1m)
    {
        // RiskManager already honors SizeMultiplier in GetPropDeskQtyFinal
        signal.SizeMultiplier = Math.Clamp(
            signal.SizeMultiplier * flow.SizeMult, 0.40m, 1.0m);
    }

    // Only WIDEN stop (never tighten) — safe vs CORE 1:1 policy
    if (flow.WiderStopLoss is decimal wsl && wsl > 0)
    {
        if (signal.Side == SignalSide.Buy && wsl < signal.StopLoss)
            signal.StopLoss = wsl;
        else if (signal.Side == SignalSide.Sell && wsl > signal.StopLoss)
            signal.StopLoss = wsl;
    }
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "[SMARTFLOW] skipped for {sym}", symbol);
}
```

## Поведение

| Условие | Действие |
|---------|----------|
| Слой выключен / нет данных | Allow (как раньше) |
| Широкий спред / тонкий top | size × SoftSizeMult |
| Экстремальный спред / book / delta / funding block | Reject `SMARTFLOW_*` |
| Cluster wall за SL | SL только шире |
| Soft adverse | size вниз, вход разрешён |

Логи: `[SMARTFLOW][SYMBOL] BLOCK|SOFT ...`
