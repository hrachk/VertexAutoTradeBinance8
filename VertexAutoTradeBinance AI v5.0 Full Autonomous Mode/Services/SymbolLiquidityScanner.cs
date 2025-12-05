using Binance.Net.Enums;

namespace VertexAutoTradeBinance8.Services
{
    public class SymbolLiquidityScanner
    {
        private readonly ILogger<SymbolLiquidityScanner> _logger;
        private readonly BinanceClientFactory _factory;

        public SymbolLiquidityScanner(
            ILogger<SymbolLiquidityScanner> logger,
            BinanceClientFactory factory)
        {
            _logger = logger;
            _factory = factory;
        }

        /// <summary>
        /// Возвращает список топ-ликвидных символов (примерно топ-30).
        /// </summary>
        public async Task<List<string>> GetTopSymbolsAsync()
        {
            using var client = _factory.CreateRestClient();

            var res = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
            if (!res.Success || res.Data == null)
                return new List<string>();

            // Фильтруем только perpetual
            var symbols = res.Data.Symbols
                .Where(s => s.ContractType == ContractType.Perpetual)
                .Select(s => s.Name)               // <-- здесь string, не объект
                .ToList();

            // Храним (symbol, score)
            var liquid = new List<(string Symbol, decimal Score)>();

            foreach (var symbol in symbols)
            {
                try
                {
                    // 1) 24h объём
                    var stat = await client.UsdFuturesApi.ExchangeData.GetTickerAsync(symbol);
                    if (!stat.Success || stat.Data == null)
                        continue;

                    decimal volumeUsd = stat.Data.QuoteVolume;

                    // 2) Глубина стакана
                    var ob = await client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(symbol, 20);
                    decimal depth = 0;
                    if (ob.Success && ob.Data != null)
                    {
                        depth =
                            ob.Data.Bids.Take(5).Sum(x => x.Quantity * x.Price) +
                            ob.Data.Asks.Take(5).Sum(x => x.Quantity * x.Price);
                    }

                    // 3) Ликвидность
                    decimal score = volumeUsd * 0.8m + depth * 0.2m;

                    liquid.Add((symbol, score));     // <-- просто symbol
                }
                catch
                {
                    // если по какому-то символу ошибка — пропускаем
                    continue;
                }
            }

            // сортируем по убыванию ликвидности
            return liquid
                .OrderByDescending(x => x.Score)
                .Take(30)
                .Select(x => x.Symbol)
                .ToList();
        }
    }
}
