# Phase 1 — Risk Engine

## Solution layout (added)

```
src/VertexAutoTrade.RiskEngine/
tests/VertexAutoTrade.RiskEngine.Tests/
```

Add to `VertexAutoTradeBinance8.sln` (Visual Studio / `dotnet sln add`):

```bash
dotnet sln VertexAutoTradeBinance8.sln add src/VertexAutoTrade.RiskEngine/VertexAutoTrade.RiskEngine.csproj
dotnet sln VertexAutoTradeBinance8.sln add tests/VertexAutoTrade.RiskEngine.Tests/VertexAutoTrade.RiskEngine.Tests.csproj
```

Wire from existing Engine project when ready:

```xml
<ProjectReference Include="..\..\src\VertexAutoTrade.RiskEngine\VertexAutoTrade.RiskEngine.csproj" />
```

## Defaults

- MaxDailyLossR = 3R  
- MaxOpenPositions = 3  
- Cool-off after 3× SL_STRATEGY_FAIL = 4h  
- Rolling 20 trades avg R < 0.2 → risk × 0.5  
- Position size strictly from stop distance (no SL expansion)
