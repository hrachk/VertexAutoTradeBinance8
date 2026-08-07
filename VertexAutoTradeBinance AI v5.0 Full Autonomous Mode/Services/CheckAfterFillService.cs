using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// CheckAfterFillService v2 — тонкий сторож поверх ProtectionOrderService.
    ///
    /// Старая версия была неработоспособна и опасна:
    ///   1) Класс BackgroundService, но регистрировался как AddSingleton →
    ///      ExecuteAsync не вызывался, сервис вообще не работал.
    ///   2) hasTP проверял FuturesOrderType.Limit, а Supervisor ставит
    ///      TakeProfitMarket → условие всегда false → сервис каждые 3 секунды
    ///      доставлял новую пачку лимитных TP.
    ///   3) Открытые ордера не фильтровались по PositionSide: в Hedge Mode стоп
    ///      на LONG засчитывался как стоп для SHORT.
    ///   4) signal = _memory.GetLastSignal(symbol) мог быть от противоположной
    ///      стороны → SL уходил не туда. Плюс сигналы алго-стратегии вообще
    ///      никогда не сохранялись (Save вызывался только из ManualPositionHandler).
    ///   5) SL ставился как FuturesOrderType.Stop (стоп-лимит) — на быстром
    ///      проливе такой ордер не исполняется.
    ///
    /// Теперь: сервис только ДЕТЕКТИРУЕТ незащищённую позицию и передаёт
    /// постановку в ProtectionOrderService. Логика стопа живёт в одном месте.
    /// TP здесь не трогаем — это зона Supervisor'а (мульти-TP, раннер, harvest).
    /// </summary>
    public class CheckAfterFillService : BackgroundService
    {
        private readonly ILogger<CheckAfterFillService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly ProtectionOrderService _protection;
        private readonly MarketDataService _marketData;
        private readonly TradeSignalMemoryService _memory;

        public CheckAfterFillService(
            ILogger<CheckAfterFillService> logger,
            BinanceClientFactory factory,
            ProtectionOrderService protection,
            MarketDataService marketData,
            TradeSignalMemoryService memory)
        {
            _logger = logger;
            _factory = factory;
            _protection = protection;
            _marketData = marketData;
            _memory = memory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogWarning("[FILL-GUARD] started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ScanAllSymbols(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[FILL-GUARD] scan error");
                }

                await Task.Delay(5000, stoppingToken);
            }
        }

        private async Task ScanAllSymbols(CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            var positions = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
            if (!positions.Success || positions.Data == null)
                return;

            var live = positions.Data.Where(p => p.Quantity != 0m).ToList();
            if (live.Count == 0)
                return;

            foreach (var pos in live)
            {
                ct.ThrowIfCancellationRequested();

                var symbol = pos.Symbol;
                var side = pos.PositionSide;

                if (await _protection.HasStopAsync(symbol, side, ct))
                    continue;

                var entry = pos.EntryPrice > 0 ? pos.EntryPrice : pos.MarkPrice;
                if (entry <= 0)
                    continue;

                var stopLevel = await ResolveStopAsync(symbol, side, entry, ct);
                if (stopLevel <= 0)
                {
                    _logger.LogError("[FILL-GUARD][{s}][{side}] уровень SL не определён", symbol, side);
                    continue;
                }

                _logger.LogWarning(
                    "[FILL-GUARD][{s}][{side}] ПОЗИЦИЯ БЕЗ SL (qty={qty}, entry={e}) → ставим",
                    symbol, side, Math.Abs(pos.Quantity), entry);

                var res = await _protection.EnsureStopAsync(symbol, side, stopLevel, ct);

                if (!res.Success)
                {
                    _logger.LogCritical(
                        "[FILL-GUARD][{s}][{side}] SL не поставлен: {reason}",
                        symbol, side, res.Reason);
                }
            }
        }

        private async Task<decimal> ResolveStopAsync(
            string symbol,
            PositionSide side,
            decimal entry,
            CancellationToken ct)
        {
            // 1) Сигнал — только если стоп на правильной стороне от входа.
            var signal = _memory.GetLastSignal(symbol);

            if (signal != null && signal.StopLoss > 0)
            {
                bool valid = side == PositionSide.Long
                    ? signal.StopLoss < entry
                    : signal.StopLoss > entry;

                if (valid)
                    return signal.StopLoss;
            }

            // 2) Фолбэк по ATR
            try
            {
                var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                if (kl == null || kl.Count < 30)
                    return 0m;

                var atr = _marketData.CalculateAtr(kl, 14);
                if (atr <= 0)
                    return 0m;

                return side == PositionSide.Long
                    ? entry - atr * 1.8m
                    : entry + atr * 1.8m;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FILL-GUARD][{s}] ATR fallback failed", symbol);
                return 0m;
            }
        }
    }
}
