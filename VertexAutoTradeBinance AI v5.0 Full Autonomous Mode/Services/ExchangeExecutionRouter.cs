using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// Phase-1 multi-exchange router.
/// CORE signals stay single pipeline; this records which venues should execute.
/// Full Bybit order placement lands in OrderExecutor in a later step.
/// </summary>
public sealed class ExchangeExecutionRouter
{
    private readonly IOptionsMonitor<ExchangeRuntimeOptions> _ex;
    private readonly BybitClientFactory _bybit;
    private readonly ILogger<ExchangeExecutionRouter> _log;

    public ExchangeExecutionRouter(
        IOptionsMonitor<ExchangeRuntimeOptions> ex,
        BybitClientFactory bybit,
        ILogger<ExchangeExecutionRouter> log)
    {
        _ex = ex;
        _bybit = bybit;
        _log = log;
    }

    public bool ShouldExecuteOnBinance() => _ex.CurrentValue.IsBinanceActive;

    public bool ShouldExecuteOnBybit()
    {
        var o = _ex.CurrentValue;
        if (!o.IsBybitActive) return false;
        return _bybit.HasCredentials();
    }

    public void LogRouting(TradeSignal signal)
    {
        if (signal == null) return;
        var b = ShouldExecuteOnBinance();
        var y = ShouldExecuteOnBybit();
        _log.LogInformation(
            "[EXCHANGE] route {sym} {side} → Binance={b} Bybit={y} mode={mode}",
            signal.Symbol, signal.Side, b, y, _ex.CurrentValue.Mode);
    }
}
