using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Helpers;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.API.Models;
using VertexAutoTradeBinance8.Strategy;
using VertexAutoTradeBinance8.Models;
using Binance.Net.Enums;

namespace VertexAutoTradeBinance8.API.Facades
{
    public class SignalFacade
    {
        private readonly StrategyEngine _strategy;
        private readonly MarketDataService _market;
        private readonly BinanceOptions _binance;
        private readonly TradingOptions _trade;

        public SignalFacade(
            StrategyEngine strategy,
            MarketDataService market,
            IOptions<BinanceOptions> binance,
            IOptions<TradingOptions> trading)
        {
            _strategy = strategy;
            _market = market;
            _binance = binance.Value;
            _trade = trading.Value;
        }

        public async Task<List<AiSignalResponse>> GetSignalsAsync(
            string? symbol,
            KlineInterval? tf,
            CancellationToken ct)
        {
            var symbols = string.IsNullOrWhiteSpace(symbol)
                ? _binance.Symbols
                : new[] { symbol };

            var timeframe = tf ?? _trade.TimeframeMinutes.ToTimeframeString().ToKlineInterval();

            var result = new List<AiSignalResponse>();

            foreach (var s in symbols.Distinct())
            {
                var klines = await _market.GetKlines(s, timeframe, 200);

                if (klines.Count == 0)
                    continue;

                var signal = _strategy.GenerateSignal(s, timeframe, klines);
                if (signal == null || signal.Side == SignalSide.None)
                    continue;

                result.Add(new AiSignalResponse
                {
                    Symbol = signal.Symbol,
                    Side = signal.Side,
                    EntryPrice = signal.EntryPrice,
                    StopLoss = signal.StopLoss,
                    TakeProfits = signal.TakeProfits ?? new List<decimal>(),
                    Atr = signal.Atr,
                    Time = signal.Time,
                    Timeframe = signal.Timeframe ?? timeframe.ToString(),
                    Strategy = "VertexAutoTradeBinance8",
                    Quality = signal.IsSuperSignal ? "super" : "normal"
                });
            }

            return result;
        }
    }
}
