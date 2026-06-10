using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.Interface;
using VertexAutoTradeBinance8.Services.MarketData;

namespace VertexAutoTradeBinance8.Services
{
    // =====================================================
    // LiquidationRiskEngine
    //
    // Профессиональный расчёт цены ликвидации и управление
    // риском позиции ДО открытия и В РЕАЛЬНОМ ВРЕМЕНИ.
    //
    // Формула Binance (USDM Futures, Isolated/Cross):
    //
    // Liq Price (Long)  = EP - (WB - MM + EP*PS*MMRB - cumB) / PS
    // Liq Price (Short) = EP + (WB - MM - EP*PS*MMRB + cumB) / PS
    //
    // Где:
    //   EP  = Entry Price
    //   WB  = Wallet Balance (Isolated = initial margin, Cross = total wallet)
    //   PS  = Position Size (qty)
    //   MMRB = Maintenance Margin Rate (зависит от notional)
    //   cumB = Maintenance Amount (зависит от notional tier)
    //   MM  = MMRB * PS * EP - cumB  (maintenance margin)
    //
    // Simplified Isolated Margin:
    //   Liq (Long)  = EP * (1 - 1/leverage + MMR)
    //   Liq (Short) = EP * (1 + 1/leverage - MMR)
    //
    // =====================================================
    public sealed class LiquidationRiskEngine : BackgroundService
    {
        private readonly ILogger<LiquidationRiskEngine> _logger;
        private readonly IAccountStateService _accountState;
        private readonly BinanceClientFactory _factory;
        private readonly FundingRateService _fundingRate;
        private readonly PositionSupervisorService _supervisor;

        // =====================================================
        // Maintenance Margin Rate тiers для USDM Futures
        // Source: Binance Leverage & Margin page
        // Notional (USDT) → (MMR, cum)
        // =====================================================
        private static readonly (decimal MaxNotional, decimal MMR, decimal Cum)[] MmrTiers =
        {
            (    50_000m, 0.0040m,       0m),
            (   250_000m, 0.0050m,      50m),
            (   500_000m, 0.0100m,    1_300m),
            ( 1_000_000m, 0.0250m,    8_800m),
            ( 2_000_000m, 0.0500m,   33_800m),
            ( 5_000_000m, 0.1000m,  133_800m),
            (10_000_000m, 0.1250m,  258_800m),
            (20_000_000m, 0.1500m,  508_800m),
            (50_000_000m, 0.2500m, 2_508_800m),
            (decimal.MaxValue, 0.5000m, 12_508_800m),
        };

        // =====================================================
        // Пороги риска
        // =====================================================
        private const decimal LIQBUFFER_WARN    = 0.15m;  // предупреждение если до ликвидации < 15%
        private const decimal LIQBUFFER_DANGER  = 0.08m;  // опасность если < 8%
        private const decimal LIQBUFFER_CRITICAL = 0.04m; // критично если < 4% → режем позицию

        // Интервал мониторинга
        private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(10);

        public LiquidationRiskEngine(
            ILogger<LiquidationRiskEngine> logger,
            IAccountStateService accountState,
            BinanceClientFactory factory,
            FundingRateService fundingRate,
            PositionSupervisorService supervisor)
        {
            _logger      = logger;
            _accountState = accountState;
            _factory      = factory;
            _fundingRate  = fundingRate;
            _supervisor   = supervisor;
        }

        // =====================================================
        // ПУБЛИЧНОЕ API
        // =====================================================

        // ----------------------
        // Результат pre-trade расчёта
        // ----------------------
        public sealed class PreTradeRiskResult
        {
            public bool   IsAllowed          { get; init; }
            public string BlockReason        { get; init; } = string.Empty;

            public decimal LiquidationPrice  { get; init; }
            public decimal LiqBufferPct      { get; init; }  // расстояние до ликвидации в % от entry
            public decimal SafeQty           { get; init; }  // безопасное количество
            public decimal SafeNotional      { get; init; }
            public decimal SafeMargin        { get; init; }
            public decimal EffectiveLeverage { get; init; }
            public decimal MaintenanceMargin { get; init; }
            public string  MmrTier           { get; init; } = string.Empty;
        }

        // ----------------------
        // Результат мониторинга открытой позиции
        // ----------------------
        public sealed class PositionLiqRisk
        {
            public string  Symbol           { get; init; } = string.Empty;
            public PositionSide Side        { get; init; }
            public decimal MarkPrice        { get; init; }
            public decimal LiquidationPrice { get; init; }
            public decimal LiqBufferPct     { get; init; }
            public LiqRiskLevel RiskLevel   { get; init; }
            public decimal SuggestedReducePct { get; init; } // сколько % позиции срезать
        }

        public enum LiqRiskLevel { Safe, Warning, Danger, Critical }

        // =====================================================
        // PRE-TRADE CHECK
        // Вызывается ИЗ RiskManager ПЕРЕД открытием позиции.
        // Проверяет: безопасно ли открывать qty по сигналу.
        // Если нет — возвращает скорректированный безопасный qty.
        // =====================================================
        public PreTradeRiskResult CheckPreTrade(
            TradeSignal signal,
            decimal qty,
            decimal walletBalance,
            decimal leverage)
        {
            decimal entry  = signal.EntryPrice;
            decimal sl     = signal.StopLoss;
            bool    isLong = signal.Side == SignalSide.Buy;

            if (entry <= 0 || qty <= 0 || walletBalance <= 0 || leverage <= 0)
                return Allow(qty, entry, leverage, 0, 0, "INVALID_PARAMS");

            decimal notional      = qty * entry;
            decimal initialMargin = notional / leverage;

            // Получаем MMR для данного notional tier
            var (mmr, cum) = GetMmrAndCum(notional);
            decimal maintenanceMargin = notional * mmr - cum;

            // =====================================================
            // Цена ликвидации (Isolated Margin формула Binance)
            // WB = initialMargin (isolated)
            // Liq Long  = EP - (WB - MM) / PS
            // Liq Short = EP + (WB - MM) / PS
            // =====================================================
            decimal liqPrice = CalcLiqPrice(entry, qty, initialMargin, maintenanceMargin, isLong);

            // Буфер до ликвидации
            decimal liqBuffer = entry > 0
                ? Math.Abs(liqPrice - entry) / entry
                : 1m;

            // =====================================================
            // SL должен быть ДО цены ликвидации с буфером 20%
            // То есть: |entry - sl| <= |entry - liq| * 0.80
            // Если SL ближе к ликвидации чем 20% — опасно
            // =====================================================
            decimal slDistance  = Math.Abs(entry - sl);
            decimal liqDistance = Math.Abs(entry - liqPrice);
            decimal slVsLiqRatio = liqDistance > 0 ? slDistance / liqDistance : 1m;

            if (slVsLiqRatio > 0.90m)
            {
                // SL слишком близко к ликвидации — уменьшаем qty
                decimal safeQty = CalcSafeQtyForBuffer(
                    entry, sl, walletBalance, leverage, isLong, mmr, cum,
                    targetBuffer: LIQBUFFER_WARN);

                return new PreTradeRiskResult
                {
                    IsAllowed          = safeQty > 0,
                    BlockReason        = safeQty <= 0 ? "SL_TOO_CLOSE_TO_LIQUIDATION" : string.Empty,
                    LiquidationPrice   = liqPrice,
                    LiqBufferPct       = liqBuffer,
                    SafeQty            = safeQty,
                    SafeNotional       = safeQty * entry,
                    SafeMargin         = safeQty * entry / leverage,
                    EffectiveLeverage  = leverage,
                    MaintenanceMargin  = maintenanceMargin,
                    MmrTier            = $"MMR={mmr:P2} cum={cum:F0}"
                };
            }

            // =====================================================
            // Проверка: margin не превышает MaxMarginPercent
            // Этот cap уже применяется в RiskManager.GetPropDeskQtyFinal
            // Здесь только проверяем SL vs liquidation distance
            // =====================================================

            return Allow(qty, entry, leverage, liqPrice, liqBuffer, string.Empty);
        }

        // =====================================================
        // BACKGROUND MONITOR
        // Проверяет открытые позиции каждые 10 сек.
        // При критическом риске — частично закрывает позицию.
        // =====================================================
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[LIQ-RISK] Monitor started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await MonitorOpenPositionsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[LIQ-RISK] Monitor error");
                }

                await Task.Delay(MonitorInterval, stoppingToken);
            }
        }

        private async Task MonitorOpenPositionsAsync(CancellationToken ct)
        {
            var positions = _accountState.GetPositions();
            if (positions.Count == 0) return;

            foreach (var pos in positions)
            {
                if (ct.IsCancellationRequested) break;
                if (pos.Qty <= 0 || pos.MarkPrice <= 0) continue;

                // Берём цену ликвидации из WS данных (LiquidationPrice из UserData)
                decimal liqPrice = pos.LiquidationPrice ?? 0m;

                // Если нет из WS — считаем сами
                if (liqPrice <= 0)
                {
                    bool isLong     = pos.Side == PositionSide.Long;
                    decimal notional = pos.Qty * pos.EntryPrice;
                    decimal leverage = pos.Leverage ?? 10;
                    decimal margin   = notional / leverage;
                    var (mmr, cum)   = GetMmrAndCum(notional);
                    decimal mm       = notional * mmr - cum;

                    liqPrice = CalcLiqPrice(pos.EntryPrice, pos.Qty, margin, mm, isLong);
                }

                if (liqPrice <= 0) continue;

                decimal mark      = pos.MarkPrice;
                decimal liqBuffer = liqPrice > 0 && mark > 0
                    ? Math.Abs(mark - liqPrice) / mark
                    : 1m;

                var riskLevel = liqBuffer >= LIQBUFFER_WARN   ? LiqRiskLevel.Safe     :
                                liqBuffer >= LIQBUFFER_DANGER  ? LiqRiskLevel.Warning  :
                                liqBuffer >= LIQBUFFER_CRITICAL ? LiqRiskLevel.Danger  :
                                                                  LiqRiskLevel.Critical;

                if (riskLevel == LiqRiskLevel.Safe) continue;

                // Логируем предупреждение
                _logger.LogWarning(
                    "[LIQ-RISK] {symbol} {side} mark={mark:F4} liq={liq:F4} buffer={buf:P2} → {level}",
                    pos.Symbol, pos.Side, mark, liqPrice, liqBuffer, riskLevel);

                // =====================================================
                // ДЕЙСТВИЯ ПО УРОВНЮ РИСКА
                // =====================================================
                switch (riskLevel)
                {
                    case LiqRiskLevel.Warning:
                        // Только логируем — SL должен сработать раньше
                        break;

                    case LiqRiskLevel.Danger:
                        // Предупреждаем и готовимся к частичному закрытию
                        _logger.LogWarning(
                            "[LIQ-RISK] DANGER {symbol} {side} — liquidation buffer < {buf:P0}! SL должен защитить.",
                            pos.Symbol, pos.Side, LIQBUFFER_DANGER);
                        break;

                    case LiqRiskLevel.Critical:
                        // Критично — частично закрываем позицию немедленно
                        decimal reduceQty = Math.Round(pos.Qty * 0.5m, 8); // закрываем 50%
                        if (reduceQty > 0)
                        {
                            _logger.LogError(
                                "[LIQ-RISK] CRITICAL {symbol} {side} buffer={buf:P2} < {thresh:P0} → EMERGENCY REDUCE 50%",
                                pos.Symbol, pos.Side, liqBuffer, LIQBUFFER_CRITICAL);

                            await EmergencyReduceAsync(pos, reduceQty, ct);
                        }
                        break;
                }
            }
        }

        // Cooldown: не пытаемся снова если предыдущая попытка провалилась
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>
            _emergencyCooldown = new(StringComparer.OrdinalIgnoreCase);

        // Кэш stepSize чтобы не вызывать ExchangeInfo каждый раз
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, decimal>
            _stepSizeCache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan EmergencyCooldown = TimeSpan.FromSeconds(60);

        private async Task EmergencyReduceAsync(
            LivePositionState pos,
            decimal reduceQty,
            CancellationToken ct)
        {
            var cooldownKey = $"{pos.Symbol}_{pos.Side}";

            // Проверяем cooldown — не спамим Binance при повторных ошибках
            if (_emergencyCooldown.TryGetValue(cooldownKey, out var lastAttempt) &&
                DateTime.UtcNow - lastAttempt < EmergencyCooldown)
            {
                _logger.LogDebug(
                    "[LIQ-RISK] Emergency reduce cooldown active for {symbol} {side}",
                    pos.Symbol, pos.Side);
                return;
            }

            _emergencyCooldown[cooldownKey] = DateTime.UtcNow;

            try
            {
                using var client = _factory.CreateRestClient();

                // =====================================================
                // Получаем stepSize для округления qty (с кэшем)
                // "Precision is over the maximum" = qty не округлен
                // =====================================================
                decimal step = 0.001m;

                if (!_stepSizeCache.TryGetValue(pos.Symbol, out step) || step <= 0)
                {
                    step = 0.001m; // fallback

                    var info = await client.UsdFuturesApi.ExchangeData
                        .GetExchangeInfoAsync(ct);

                    if (info.Success && info.Data != null)
                    {
                        var sym = info.Data.Symbols.FirstOrDefault(s =>
                            s.Name.Equals(pos.Symbol, StringComparison.OrdinalIgnoreCase));

                        if (sym?.LotSizeFilter?.StepSize > 0)
                            step = sym.LotSizeFilter.StepSize;
                    }

                    _stepSizeCache[pos.Symbol] = step;
                }

                // Округляем qty по stepSize
                decimal roundedQty = Math.Floor(reduceQty / step) * step;

                if (roundedQty <= 0)
                {
                    _logger.LogWarning(
                        "[LIQ-RISK] Emergency reduce qty={qty} rounded to 0 by step={step} for {symbol}",
                        reduceQty, step, pos.Symbol);
                    return;
                }

                // Не закрываем больше чем есть
                roundedQty = Math.Min(roundedQty, Math.Floor(pos.Qty / step) * step);

                var closeSide = pos.Side == PositionSide.Long
                    ? Binance.Net.Enums.OrderSide.Sell
                    : Binance.Net.Enums.OrderSide.Buy;

                var result = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol:                  pos.Symbol,
                    side:                    closeSide,
                    type:                    FuturesOrderType.Market,
                    quantity:                roundedQty,
                    positionSide:            pos.Side,
                    selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                    ct:                      ct);

                if (result.Success)
                {
                    // Успех — сбрасываем cooldown
                    _emergencyCooldown.TryRemove(cooldownKey, out _);

                    _logger.LogWarning(
                        "[LIQ-RISK] Emergency reduce OK {symbol} {side} qty={qty} (step={step})",
                        pos.Symbol, pos.Side, roundedQty, step);
                }
                else
                {
                    _logger.LogError(
                        "[LIQ-RISK] Emergency reduce FAILED {symbol}: code={code} msg={msg} qty={qty} step={step}",
                        pos.Symbol,
                        result.Error?.Code,
                        result.Error?.Message,
                        roundedQty,
                        step);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LIQ-RISK] Emergency reduce exception {symbol}", pos.Symbol);
            }
            }
        }

        // =====================================================
        // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
        // =====================================================

        // Формула Binance для цены ликвидации (Isolated)
        public static decimal CalcLiqPrice(
            decimal entry,
            decimal qty,
            decimal walletBalance,
            decimal maintenanceMargin,
            bool isLong)
        {
            if (qty <= 0) return 0m;

            // Liq Long  = EP - (WB - MM) / PS
            // Liq Short = EP + (WB - MM) / PS
            decimal delta = (walletBalance - maintenanceMargin) / qty;
            return isLong
                ? Math.Max(0m, entry - delta)
                : entry + delta;
        }

        // Находим безопасный qty при котором буфер до ликвидации >= targetBuffer
        private decimal CalcSafeQtyForBuffer(
            decimal entry,
            decimal sl,
            decimal walletBalance,
            decimal leverage,
            bool isLong,
            decimal mmr,
            decimal cum,
            decimal targetBuffer)
        {
            // Ищем qty при котором liqBuffer >= targetBuffer
            // Binary search между minQty и maxQty
            decimal maxQty  = walletBalance * leverage / entry;
            decimal minQty  = 0.001m;
            decimal safeQty = 0m;

            for (int i = 0; i < 50; i++) // 50 итераций достаточно
            {
                decimal mid     = (minQty + maxQty) / 2m;
                decimal notional = mid * entry;
                decimal margin  = notional / leverage;
                decimal mm      = notional * mmr - cum;
                decimal liq     = CalcLiqPrice(entry, mid, margin, mm, isLong);
                decimal buffer  = entry > 0 ? Math.Abs(liq - entry) / entry : 0m;

                if (buffer >= targetBuffer)
                {
                    safeQty = mid;
                    minQty  = mid;
                }
                else
                {
                    maxQty = mid;
                }
            }

            return Math.Round(safeQty, 6);
        }

        // Получаем MMR и cum для данного notional
        public static (decimal mmr, decimal cum) GetMmrAndCum(decimal notional)
        {
            foreach (var tier in MmrTiers)
            {
                if (notional <= tier.MaxNotional)
                    return (tier.MMR, tier.Cum);
            }
            return (0.50m, 12_508_800m);
        }

        // Считаем суммарную используемую маржу открытых позиций
        private decimal GetCurrentUsedMargin()
        {
            decimal used = 0m;
            foreach (var pos in _accountState.GetPositions())
            {
                if (pos.IsolatedMargin.HasValue && pos.IsolatedMargin > 0)
                    used += pos.IsolatedMargin.Value;
                else if (pos.Qty > 0 && pos.EntryPrice > 0 && pos.Leverage.HasValue && pos.Leverage > 0)
                    used += pos.Qty * pos.EntryPrice / pos.Leverage.Value;
            }
            return used;
        }

        private static PreTradeRiskResult Allow(
            decimal qty,
            decimal entry,
            decimal leverage,
            decimal liqPrice,
            decimal liqBuffer,
            string reason)
        {
            return new PreTradeRiskResult
            {
                IsAllowed          = true,
                BlockReason        = reason,
                LiquidationPrice   = liqPrice,
                LiqBufferPct       = liqBuffer,
                SafeQty            = qty,
                SafeNotional       = qty * entry,
                SafeMargin         = qty * entry / (leverage > 0 ? leverage : 1m),
                EffectiveLeverage  = leverage,
            };
        }
    }
}
