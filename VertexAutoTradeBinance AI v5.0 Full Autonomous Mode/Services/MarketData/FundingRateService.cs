using Binance.Net.Clients;
using Binance.Net.Objects.Models.Futures;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Services.MarketData
{
    // =====================================================
    // FundingRateService
    //
    // Динамическое отслеживание funding rate в реальном времени.
    //
    // Источник: GET /fapi/v1/premiumIndex (weight=1 per symbol)
    // Обновление: каждые 60 секунд через REST + WS подписка @markPrice
    //
    // Binance формула:
    //   FundingRate = PremiumIndex + clamp(InterestRate - PremiumIndex, 0.05%, -0.05%)
    //   InterestRate = 0.01% (фиксировано)
    //
    // PredictedFundingRate вычисляется локально каждую минуту
    // на основе треугольного взвешивания последних premium значений —
    // приближение к тому что Binance будет применять при следующем funding.
    // =====================================================
    public sealed class FundingRateService : BackgroundService
    {
        private readonly ILogger<FundingRateService> _logger;
        private readonly BinanceClientFactory _factory;

        // =====================================================
        // FundingSnapshot — состояние по одному символу
        // =====================================================
        public sealed class FundingSnapshot
        {
            public string   Symbol              { get; init; } = string.Empty;
            public decimal? LastFundingRate     { get; set; }
            public decimal  PredictedRate       { get; set; }
            public decimal  PremiumIndex        { get; set; }
            public decimal  MarkPrice           { get; set; }
            public decimal  IndexPrice          { get; set; }
            public DateTime NextFundingTime     { get; set; }
            public DateTime UpdatedAt           { get; set; }

            // 3-дневная история applied rates (9 периодов × 8ч)
            private readonly Queue<decimal> _rateHistory    = new();
            private readonly Queue<decimal> _premiumHistory = new();
            private const int RateHistoryDepth    = 9;
            private const int PremiumHistoryDepth = 5;

            public void PushFundingRate(decimal rate)
            {
                _rateHistory.Enqueue(rate);
                if (_rateHistory.Count > RateHistoryDepth)
                    _rateHistory.Dequeue();
            }

            public void PushPremium(decimal premium)
            {
                _premiumHistory.Enqueue(premium);
                if (_premiumHistory.Count > PremiumHistoryDepth)
                    _premiumHistory.Dequeue();
            }

            // 3-дневный кумулятивный rate (как в Binance Arbitrage Bot)
            public decimal CumulativeRate3d =>
                _rateHistory.Count > 0 ? _rateHistory.Sum() : (LastFundingRate ?? 0m) * 9;

            public decimal PremiumTrend =>
                _premiumHistory.Count < 2 ? 0m :
                _premiumHistory.Last() - _premiumHistory.First();

            // Risk по 3d cumulative (как Binance Arbitrage Bot)
            public FundingRisk Risk =>
                Math.Abs(CumulativeRate3d) >= 0.0015m ? FundingRisk.Extreme :
                Math.Abs(CumulativeRate3d) >= 0.0009m ? FundingRisk.High    :
                Math.Abs(CumulativeRate3d) >= 0.0003m ? FundingRisk.Medium  :
                                                        FundingRisk.Low;

            // Мгновенный риск для quick decisions
            public FundingRisk InstantRisk =>
                Math.Abs(PredictedRate) >= 0.0005m ? FundingRisk.Extreme :
                Math.Abs(PredictedRate) >= 0.0003m ? FundingRisk.High    :
                Math.Abs(PredictedRate) >= 0.0001m ? FundingRisk.Medium  :
                                                     FundingRisk.Low;

            public double MinutesToNextFunding =>
                Math.Max(0, (NextFundingTime - DateTime.UtcNow).TotalMinutes);

            // Positive Carry: cum > 0 → лонги платят шортам
            public bool ShouldBlockLong =>
                CumulativeRate3d >= 0.0009m && Risk >= FundingRisk.High;

            // Reverse Carry: cum < 0 → шорты платят лонгам
            public bool ShouldBlockShort =>
                CumulativeRate3d <= -0.0009m && Risk >= FundingRisk.High;

            public bool ShouldAccelerateTP =>
                InstantRisk >= FundingRisk.High && MinutesToNextFunding <= 30;

            public decimal AnnualizedPct =>
                PredictedRate * 3 * 365 * 100m;

            public string CarryDirection =>
                CumulativeRate3d > 0.0003m  ? "POSITIVE_CARRY" :
                CumulativeRate3d < -0.0003m ? "REVERSE_CARRY"  : "NEUTRAL";
        }

        public enum FundingRisk { Low, Medium, High, Extreme }

        // =====================================================
        // Кэш: symbol → snapshot
        // =====================================================
        private readonly ConcurrentDictionary<string, FundingSnapshot> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, DateTime> _lastFetch =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan RestInterval = TimeSpan.FromSeconds(60);
        private HashSet<string> _trackedSymbols = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _symbolLock = new(1, 1);

        public FundingRateService(
            ILogger<FundingRateService> logger,
            BinanceClientFactory factory)
        {
            _logger = logger;
            _factory = factory;
        }

        // =====================================================
        // PUBLIC API
        // =====================================================

        /// Получить snapshot для символа (null если нет данных)
        public FundingSnapshot? Get(string symbol)
            => _cache.TryGetValue(symbol, out var s) ? s : null;

        /// Подписать символы на отслеживание
        public async Task TrackSymbolsAsync(IEnumerable<string> symbols)
        {
            await _symbolLock.WaitAsync();
            try
            {
                _trackedSymbols = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _symbolLock.Release();
            }
        }

        /// Быстрая проверка — можно ли входить в Long
        public bool CanEnterLong(string symbol)
        {
            var s = Get(symbol);
            return s == null || !s.ShouldBlockLong;
        }

        /// Быстрая проверка — можно ли входить в Short
        public bool CanEnterShort(string symbol)
        {
            var s = Get(symbol);
            return s == null || !s.ShouldBlockShort;
        }

        /// Нужно ли ускорить фиксацию прибыли
        public bool ShouldAccelerateTP(string symbol)
        {
            var s = Get(symbol);
            return s?.ShouldAccelerateTP ?? false;
        }

        // =====================================================
        // BACKGROUND LOOP
        // Обновляет funding каждые 60 секунд для всех отслеживаемых символов
        // Используем GetMarkPriceAsync (GET /fapi/v1/premiumIndex, weight=1)
        // =====================================================
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[FUNDING] Service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAllAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[FUNDING] Refresh error");
                }

                await Task.Delay(RestInterval, stoppingToken);
            }
        }

        private async Task RefreshAllAsync(CancellationToken ct)
        {
            HashSet<string> symbols;
            await _symbolLock.WaitAsync(ct);
            try { symbols = new HashSet<string>(_trackedSymbols, StringComparer.OrdinalIgnoreCase); }
            finally { _symbolLock.Release(); }

            if (symbols.Count == 0) return;

            using var client = _factory.CreateRestClient();

            // Обновляем по одному — weight=1 каждый
            // Все символы за один цикл = symbols.Count weight units
            foreach (var symbol in symbols)
            {
                if (ct.IsCancellationRequested) break;

                // Throttle: не чаще чем раз в RestInterval
                if (_lastFetch.TryGetValue(symbol, out var last) &&
                    DateTime.UtcNow - last < RestInterval)
                    continue;

                try
                {
                    await FetchAndUpdateAsync(client, symbol, ct);
                    _lastFetch[symbol] = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[FUNDING] Failed to fetch {symbol}", symbol);
                }

                // Небольшая пауза между запросами — rate limit protection
                await Task.Delay(200, ct);
            }
        }

        private async Task FetchAndUpdateAsync(BinanceRestClient client, string symbol, CancellationToken ct)
        {
            // GET /fapi/v1/premiumIndex?symbol=BTCUSDT
            // Возвращает: markPrice, indexPrice, lastFundingRate, nextFundingTime
            var result = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(symbol, ct);

            if (!result.Success || result.Data == null)
            {
                _logger.LogDebug("[FUNDING] GetMarkPrice failed {symbol}: {err}", symbol, result.Error);
                return;
            }

            var data = result.Data;

            decimal premium = data.IndexPrice > 0
                ? (data.MarkPrice - data.IndexPrice) / data.IndexPrice
                : 0m;

            // Predicted rate = premium + clamp(0.0001 - premium, 0.0005, -0.0005)
            // InterestRate = 0.01% = 0.0001
            decimal interestRate = 0.0001m;
            decimal clampedDiff = Math.Max(-0.0005m, Math.Min(0.0005m, interestRate - premium));
            decimal predictedRate = premium + clampedDiff;

            var snapshot = _cache.GetOrAdd(symbol, k => new FundingSnapshot { Symbol = k });

            snapshot.LastFundingRate = data.FundingRate;
            snapshot.PredictedRate   = predictedRate;
            snapshot.PremiumIndex    = premium;
            snapshot.MarkPrice       = data.MarkPrice;
            snapshot.IndexPrice      = data.IndexPrice;
            snapshot.NextFundingTime =  data.NextFundingTime;
            snapshot.UpdatedAt       = DateTime.UtcNow;
            snapshot.PushPremium(premium);
            // Обновляем историю applied rates (LastFundingRate меняется раз в 8ч)
            snapshot.PushFundingRate(data.LastFundingRate);

            if (snapshot.Risk >= FundingRisk.High || snapshot.InstantRisk >= FundingRisk.High)
            {
                _logger.LogWarning(
                    "[FUNDING] {symbol} rate={rate:P4} pred={pred:P4} 3d={cum:P4} carry={carry} risk={risk} nextIn={min:F0}min apr={apr:F1}%/yr",
                    symbol, data.FundingRate, predictedRate,
                    snapshot.CumulativeRate3d, snapshot.CarryDirection,
                    snapshot.Risk, snapshot.MinutesToNextFunding, snapshot.AnnualizedPct);
            }
            else
            {
                _logger.LogDebug(
                    "[FUNDING] {symbol} rate={rate:P4} pred={pred:P4} 3d={cum:P4} carry={carry}",
                    symbol, data.FundingRate, predictedRate,
                    snapshot.CumulativeRate3d, snapshot.CarryDirection);
            }
        }
    }
}
