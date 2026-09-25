# VertexAutoTrade.RiskEngine (Phase 1)

Institutional **pre-trade** risk layer. Must run **before** strategy order send.

## Components

| Type | Role |
|------|------|
| `HardRiskGuard` | Max daily loss R / equity %, max open positions, timed block |
| `AntiTiltCircuitBreaker` | 3× `SL_STRATEGY_FAIL` → cool-off; weak rolling avg R → risk ×0.5 |
| `PositionSizer` | `qty = (equity × riskFrac) / \|entry − sl\|` — never widens SL |
| `EntryRiskGate` | Hard → AntiTilt composite |

## DI

```csharp
services.Configure<HardRiskOptions>(cfg.GetSection("RiskEngine:Hard"));
services.Configure<AntiTiltOptions>(cfg.GetSection("RiskEngine:AntiTilt"));
services.AddVertexRiskEngine();
```

## Integration (existing Worker)

```csharp
var perm = _entryGate.Evaluate(snapshot, configuredBaseRisk);
if (!perm.Allowed) { /* reject signal, log Reason */ return; }
var size = _sizer.Calculate(new PositionSizeRequest(equity, entry, sl, perm.EffectiveRiskFraction, minQty, step, minNotional));
if (!size.Ok) { /* reject */ return; }
// place order with size.Quantity
```

On every closed trade:

```csharp
_hard.RegisterClosedR(realizedR, closedAt);
_tilt.OnTradeClosed(new ClosedTradeRiskEvent(closedAt, realizedR, closeReason, symbol));
```

## Tests

```bash
dotnet test tests/VertexAutoTrade.RiskEngine.Tests
```
