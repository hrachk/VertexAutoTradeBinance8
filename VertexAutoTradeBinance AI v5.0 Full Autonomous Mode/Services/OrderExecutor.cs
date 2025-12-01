using Binance.Net.Clients;
using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class OrderExecutor
    {
        private readonly ILogger<OrderExecutor> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;


        // --- анти-манип / логика входа ---
        private const decimal MaxSlipLimit = 0.0010m;     // 0.10%
        private const decimal MaxSlipForceMarket = 0.004m; // 0.4%
        private const decimal AtrManipThreshold = 0.025m;  // 2.5% ATR spike

        public OrderExecutor(ILogger<OrderExecutor> logger, BinanceClientFactory factory, SymbolInfoService symbolInfo)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
        }
        // Safe SL (fix for -2021)
        private decimal SafeClampSL(decimal rawSl, decimal markPrice, SignalSide side, decimal tick)
        {
            decimal offset = tick * 20m;

            if (side == SignalSide.Buy)
            {
                decimal maxAllowed = markPrice - offset;
                return rawSl >= maxAllowed ? maxAllowed : rawSl;
            }
            else
            {
                decimal minAllowed = markPrice + offset;
                return rawSl <= minAllowed ? minAllowed : rawSl;
            }
        }

        /*  public async Task PlaceOrderAsync(TradeSignal signal, decimal quantity, CancellationToken ct = default)
          {
              if (signal == null) throw new ArgumentNullException(nameof(signal));
              if (quantity <= 0m)
              {
                  _logger.LogWarning("[ORDER][{Symbol}] Quantity <= 0, skip", signal.Symbol);
                  return;
              }

              using var client = _factory.CreateRestClient();

              var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
              var posSide = signal.Side == SignalSide.Buy ? PositionSide.Long : PositionSide.Short;

              // === 1) Читаем текущую цену (anti-manip, mix MARKET/LIMIT) ===
              decimal markPrice = signal.EntryPrice;
              try
              {
                  var priceResult = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);
                  if (priceResult.Success && priceResult.Data != null && priceResult.Data.Price > 0)
                      markPrice = priceResult.Data.Price;
              }
              catch (Exception ex)
              {
                  _logger.LogWarning(ex, "[ORDER][{Symbol}] Failed to load mark price, use signal.EntryPrice", signal.Symbol);
              }

              var slip = Math.Abs(markPrice - signal.EntryPrice) / markPrice; // относительное отклонение
              var atrSpike = signal.Atr.HasValue
                             ? (signal.Atr.Value / signal.EntryPrice) >= MaxAtrSpikePercent
                             : false;

              // === 2) Анти-манипуляция ===
              if (atrSpike)
              {
                  _logger.LogWarning(
                      "[ORDER][{Symbol}] Anti-Manip: ATR spike (ATR={Atr}, EP={EntryPrice}), skip entry",
                      signal.Symbol, signal.Atr, signal.EntryPrice);
                  return;
              }

              // === 3) Выбор LIMIT или MARKET ===
              FuturesOrderType entryType;
              decimal? limitPrice = null;
              TimeInForce? tif = null;
              bool reduceOnly = false;

              if (slip <= MaxSlipForLimitPercent)
              {
                  // Цена рядом с желаемой – ставим LIMIT по сигналу
                  entryType = FuturesOrderType.Limit;
                  limitPrice = signal.EntryPrice;
                  tif = TimeInForce.GoodTillCanceled;

                  _logger.LogInformation(
                      "[ORDER][{Symbol}] ENTRY: use LIMIT @ {Price} (slip={Slip:P4})",
                      signal.Symbol, limitPrice, slip);
              }
              else if (slip <= MaxSlipHardRejectPercent)
              {
                  // Цена чуть ушла – MARKET, но допускаем
                  entryType = FuturesOrderType.Market;
                  _logger.LogInformation(
                      "[ORDER][{Symbol}] ENTRY: use MARKET (slip={Slip:P4})",
                      signal.Symbol, slip);
              }
              else
              {
                  // Цена уже улетела – лучше не догонять
                  _logger.LogWarning(
                      "[ORDER][{Symbol}] ENTRY rejected: price slipped too much (slip={Slip:P4}), anti-chase",
                      signal.Symbol, slip);
                  return;
              }

              // === 4) Открываем позицию ===
              var entryOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                  symbol: signal.Symbol,
                  side: side,
                  type: entryType,
                  quantity: quantity,
                  price: limitPrice,
                  positionSide: posSide,
                  timeInForce: tif,
                  reduceOnly: reduceOnly,  // Убираем reduceOnly для ордеров на открытие
                  ct: ct);

              if (!entryOrder.Success || entryOrder.Data == null)
              {
                  _logger.LogError("[ORDER][{Symbol}] ENTRY ERROR: {Error}", signal.Symbol, entryOrder.Error);
                  return;
              }

              _logger.LogInformation(
                  "[ORDER][{Symbol}] ENTRY OK type={Type} qty={Qty} price={Price}",
                  signal.Symbol, entryType, quantity, limitPrice ?? markPrice);

              // === 5) После входа – ставим защиту (SL/TP) с ретраями ===
              var protectionOk = await EnsureProtectionAsync(client, signal, quantity, side, posSide, ct);

              if (!protectionOk)
              {
                  // Если не удалось поставить SL/TP – аварийно закрываем позицию
                  _logger.LogError(
                      "[ORDER][{Symbol}] PROTECTION FAILED – emergency close position", signal.Symbol);

                  var closeSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

                  var closeOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                      symbol: signal.Symbol,
                      side: closeSide,
                      type: FuturesOrderType.Market,
                      quantity: quantity,
                      positionSide: posSide,
                      reduceOnly: true, // Используем reduceOnly только для закрытия позиции
                      ct: ct);

                  if (!closeOrder.Success)
                  {
                      _logger.LogError(
                          "[ORDER][{Symbol}] EMERGENCY CLOSE ERROR: {Error}",
                          signal.Symbol, closeOrder.Error);
                  }
                  else
                  {
                      _logger.LogWarning("[ORDER][{Symbol}] EMERGENCY CLOSE OK", signal.Symbol);
                  }
              }
          }
          */


        public async Task PlaceOrderAsync(TradeSignal sig, decimal quantity, CancellationToken ct = default)
        {
            if (sig == null) return;
            if (quantity <= 0) return;

            using var client = _factory.CreateRestClient();

            var side = sig.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
            var posSide = sig.Side == SignalSide.Buy ? PositionSide.Long : PositionSide.Short;

            // ===== LOAD PRICE =====
            decimal markPrice = sig.EntryPrice;
            try
            {
                var p = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(sig.Symbol, ct: ct);
                if (p.Success && p.Data.Price > 0)
                    markPrice = p.Data.Price;
            }
            catch { }

            decimal slip = Math.Abs(sig.EntryPrice - markPrice) / markPrice;

            // ATR spike — anti manipulation
            bool atrSpike = sig.Atr.HasValue && sig.Atr.Value / markPrice >= AtrManipThreshold;
            if (atrSpike)
            {
                _logger.LogWarning("[ORDER][{S}] ATR spike — skip entry", sig.Symbol);
                return;
            }

            // ===== Smart LIMIT / MARKET =====
            FuturesOrderType entryType;
            decimal? limitPrice = null;
            TimeInForce? tif = null;

            if (slip <= MaxSlipLimit)
            {
                entryType = FuturesOrderType.Limit;
                limitPrice = sig.EntryPrice;
                tif = TimeInForce.GoodTillCanceled;
            }
            else if (slip <= MaxSlipForceMarket)
            {
                entryType = FuturesOrderType.Market;
            }
            else
            {
                _logger.LogWarning("[ORDER][{S}] anti-chase reject slip={Slip:P3}", sig.Symbol, slip);
                return;
            }

            // ===== ENTRY ORDER =====
            var entry = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: sig.Symbol,
                side: side,
                type: entryType,
                quantity: quantity,
                price: limitPrice,
                positionSide: posSide,
                reduceOnly: false,
                timeInForce: tif,
                ct: ct);

            if (!entry.Success)
            {
                _logger.LogError("[ORDER][{S}] ENTRY ERROR: {Err}", sig.Symbol, entry.Error);
                return;
            }

            _logger.LogInformation("[ORDER][{S}] ENTRY OK", sig.Symbol);

            // ===== PROTECTION =====
            await ApplySLTPAsync(client, sig, quantity, side, posSide, markPrice, ct);
        }

        private async Task ApplySLTPAsync(
            BinanceRestClient client,
            TradeSignal sig,
            decimal quantity,
            OrderSide side,
            PositionSide posSide,
            decimal mark,
            CancellationToken ct)
        {
            decimal tick = await _symbolInfo.GetTickSizeAsync(sig.Symbol);
            decimal safeSL = SafeClampSL(sig.StopLoss, mark, sig.Side, tick);

            // ===== SL =====
            var slSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

            var sl = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: sig.Symbol,
                side: slSide,
                type: FuturesOrderType.StopMarket,
                stopPrice: safeSL,
                quantity: quantity,
                positionSide: posSide,
                reduceOnly: true,
                workingType: WorkingType.Mark,
                closePosition: false,
                ct: ct);

            if (!sl.Success)
            {
                _logger.LogError("[ORDER][{S}] SL ERROR {Err}", sig.Symbol, sl.Error);
                return;
            }

            // ===== TP =====
            if (sig.TakeProfits == null || sig.TakeProfits.Count == 0)
                return;

            var tpSide = slSide;
            decimal part = quantity / sig.TakeProfits.Count;

            foreach (var tp in sig.TakeProfits)
            {
                var t = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: sig.Symbol,
                    side: tpSide,
                    type: FuturesOrderType.TakeProfitMarket,
                    stopPrice: tp,
                    quantity: part,
                    positionSide: posSide,
                    reduceOnly: true,
                    workingType: WorkingType.Mark,
                    closePosition: false,
                    ct: ct);

                if (!t.Success)
                {
                    _logger.LogError("[ORDER][{S}] TP ERROR {Err}", sig.Symbol, t.Error);
                    return;
                }
            }

            _logger.LogInformation("[ORDER][{S}] SL/TP OK", sig.Symbol);
        }

        /// <summary>
        /// Ставит/переставляет SL и TP c несколькими попытками.
        /// Возвращает true, если всё ок, иначе false.
        /// </summary>
        private async Task<bool> EnsureProtectionAsync(
            BinanceRestClient client,
            TradeSignal signal,
            decimal quantity,
            OrderSide side,
            PositionSide posSide,
            CancellationToken ct)
        {
            const int maxAttempts = 3;

            // === SL ===
            var slOk = await TryPlaceWithRetryAsync(
                maxAttempts,
                async attempt =>
                {
                    if (signal.StopLoss <= 0)
                        return true; // нет SL в сигнале – считаем нормой

                    var slSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

                    var sl = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: signal.Symbol,
                        side: slSide,
                        type: FuturesOrderType.StopMarket,
                        quantity: quantity,
                        stopPrice: signal.StopLoss,
                        positionSide: posSide,
                        reduceOnly: true,
                        workingType: WorkingType.Mark,
                        closePosition: false,
                        ct: ct);

                    if (!sl.Success)
                    {
                        _logger.LogWarning(
                            "[ORDER][{Symbol}] SL attempt {Attempt}/{Max} ERROR: {Error}",
                            signal.Symbol, attempt, maxAttempts, sl.Error);
                        return false;
                    }

                    _logger.LogInformation(
                        "[ORDER][{Symbol}] SL placed @ {Price} (attempt {Attempt})",
                        signal.Symbol, signal.StopLoss, attempt);
                    return true;
                });

            if (!slOk) return false;

            // === TP (multi-TP) ===
            if (signal.TakeProfits == null || signal.TakeProfits.Count == 0)
                return true; // только SL – тоже ок

            var tpSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var partQty = quantity / signal.TakeProfits.Count;

            for (var i = 0; i < signal.TakeProfits.Count; i++)
            {
                var tpPrice = signal.TakeProfits[i];

                var tpOk = await TryPlaceWithRetryAsync(
                    maxAttempts,
                    async attempt =>
                    {
                        var tp = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                            symbol: signal.Symbol,
                            side: tpSide,
                            type: FuturesOrderType.TakeProfitMarket,
                            quantity: partQty,
                            stopPrice: tpPrice,
                            positionSide: posSide,
                            reduceOnly: true,
                            workingType: WorkingType.Mark,
                            closePosition: false,
                            ct: ct);

                        if (!tp.Success)
                        {
                            _logger.LogWarning(
                                "[ORDER][{Symbol}] TP#{Idx} attempt {Attempt}/{Max} ERROR: {Error}",
                                signal.Symbol, i + 1, attempt, maxAttempts, tp.Error);
                            return false;
                        }

                        _logger.LogInformation(
                            "[ORDER][{Symbol}] TP#{Idx} placed @ {Price} qty={Qty} (attempt {Attempt})",
                            signal.Symbol, i + 1, tpPrice, partQty, attempt);
                        return true;
                    });

                if (!tpOk)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Универсальный helper для ретраев.
        /// </summary>
        private static async Task<bool> TryPlaceWithRetryAsync(
            int maxAttempts,
            Func<int, Task<bool>> action)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var ok = await action(attempt);
                if (ok) return true;

                // небольшая пауза перед повтором
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }

            return false;
        }
    }
}
