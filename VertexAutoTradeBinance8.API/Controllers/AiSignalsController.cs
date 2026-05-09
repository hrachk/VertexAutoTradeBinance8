using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.API.Binders;
using VertexAutoTradeBinance8.API.Models;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Helpers;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Strategy;


namespace VertexAutoTradeBinance8.API.Controllers;

[ApiController]
[Route("api/ai/[controller]")]
public class SignalsController : ControllerBase
{
    private readonly ILogger<SignalsController> _logger;
    private readonly BinanceOptions _binanceOptions;
    private readonly TradingOptions _tradingOptions;
    private readonly MarketDataService _marketData;
    private readonly StrategyEngine _strategy;

    public SignalsController(
        ILogger<SignalsController> logger,
        IOptions<BinanceOptions> binanceOptions,
        IOptions<TradingOptions> tradingOptions,
        MarketDataService marketData,
        StrategyEngine strategy)
    {
        _logger = logger;
        _binanceOptions = binanceOptions.Value;
        _tradingOptions = tradingOptions.Value;
        _marketData = marketData;
        _strategy = strategy;
    }

    /// <summary>
    /// Возвращает актуальные сигналы по символам.
    /// Если symbol не указан – по всем символам из BinanceOptions.Symbols.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AiSignalResponse>>> GetSignals(
     [FromQuery] string? symbol = null,
     [FromQuery][KlineIntervalBinder] KlineInterval? timeframe = null,
     CancellationToken ct = default)
    {
        var symbols = string.IsNullOrWhiteSpace(symbol)
            ? _binanceOptions.Symbols
            : new[] { symbol };

        // если юзер не передал timeframe → берём из настроек
        var tf = timeframe ??
            _tradingOptions.TimeframeMinutes.ToTimeframeString().ToKlineInterval();

        var result = new List<AiSignalResponse>();

        foreach (var s in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var klines = await _marketData.GetKlines(s, tf, 200);
                if (klines.Count == 0)
                    continue;

                var signal = _strategy.GenerateSignal(s, tf, klines);
                if (signal == null || signal.Side == SignalSide.None)
                    continue;

                result.Add(new AiSignalResponse
                {
                    Symbol = signal.Symbol,
                    Side = signal.Side,
                    EntryPrice = signal.EntryPrice,
                    StopLoss = signal.StopLoss,
                    TakeProfits = signal.TakeProfits ?? new List<decimal>(),
                    Timeframe = signal.Timeframe ?? tf.ToString(),
                    Time = signal.Time,
                    Atr = signal.Atr,
                    Strategy = "VertexAutoTradeBinance8",
                    Quality = signal.IsSuperSignal ? "super" : "normal"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating signal for {Symbol}", s);
            }
        }

        return Ok(result);
    }


}
