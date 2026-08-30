using VertexAutoTradeBinance8.Services.MarketData;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.Channels;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Bootstrap;
using VertexAutoTradeBinance8.Services.Engine;
using VertexAutoTradeBinance8.Services.Formatting;
using VertexAutoTradeBinance8.Strategy;
using static VertexAutoTradeBinance8.Services.AiTimeframeSelectorService;

namespace VertexAutoTradeBinance8
{

    public sealed class RealtimePriceService
    {
        private readonly ConcurrentDictionary<string, decimal> _lastPrice = new();

        public void Update(string symbol, decimal price)
        {
            _lastPrice[symbol] = price;
        }

        public bool TryGet(string symbol, out decimal price)
        {
            return _lastPrice.TryGetValue(symbol, out price);
        }

        public decimal GetOrDefault(string symbol)
        {
            return _lastPrice.TryGetValue(symbol, out var p) ? p : 0m;
        }
    }



    public class TradingWorker : BackgroundService
    {
        private readonly ILogger<TradingWorker> _logger;
        private readonly TradingOptions _options;
        private readonly TradingOptionsResolver _resolver;

        private readonly MarketDataService _market;
        private readonly MarketDataFacade _marketDataFacade;
        private readonly StrategyEngine _strategy;
        private readonly StrategyRouter _strategyRouter;
        private readonly RiskManager _risk;
        private readonly OrderExecutor _executor;
        private readonly BybitOrderExecutor _bybitExecutor;
        private readonly ExchangeExecutionRouter _exchangeRouter;
        private readonly VertexAutoTradeBinance8.Services.HistoricalData.DataDbSymbolFeed? _dataDbFeed;
        private readonly BinanceClientFactory _factory;
        private readonly LiquidityGuardService _liq;
        private readonly OrderCleanerService _cleaner;
        private readonly PredictiveEngineV4ConfirmationService _predict;
        private readonly IOptionsMonitor<VertexAutoTradeBinance8.Configuration.SignalConfidenceSettings> _confSettings;
        private readonly PositionSupervisorService _supervisor;
        private readonly AiStopLossOptimizer _slOpt;
        private readonly AiRiskScalerV2 _riskScaler;
        private readonly AiSelfLearningService _learn;
        private readonly AiModelSnapshotService _snapshot;

        private readonly Dictionary<string, DateTime> _lastTrade = new();
        private readonly SymbolRegistryService _symbols;
        private readonly AiTimeframeSelectorService _tfSelector;
        private readonly EngineStateBuilder _engineStateBuilder;
        private readonly EngineStateSnapshotService _engineStateSnapshot;
        private readonly IBootGate _bootGate;
        private readonly IStrategyPreFilter _pre;
        private readonly MarketContextService _marketContext;
        private readonly SimulatedTradeService _sim;
        private readonly LiveSignalService _liveSig;
        private readonly SymbolInfoService _symbolInfo;
        private readonly FundingRateService _fundingRate;
        private readonly RealtimeMomentumDetector _momentum;
        private readonly TradeStateManager _tradeState;

        private DateTime _lastQuantTick = DateTime.UtcNow;


        // ===== Engine heartbeat / CPM =====
        private int _cycleCounter = 0;
        private DateTime _cycleWindowUtc = DateTime.UtcNow;

        // Optional: чтобы не писать слишком часто, но можно и без этого
        private DateTime _lastEngineTickUtc = DateTime.MinValue;
        private readonly AiMarketRegimeService _marketRegime;


        // ===============================
        // TRACKED SYMBOLS (positions-safe)
        // ===============================
        private readonly ConcurrentDictionary<string, byte> _tracked = new();
        private readonly ConcurrentDictionary<string, byte> _warm = new();

        // REST throttle for positions scan (avoid spam)
        private DateTime _lastPositionsScanUtc = DateTime.MinValue;
        private IReadOnlyList<string> _cachedPositionSymbols = Array.Empty<string>();

        // Safety: if symbol was tracked, keep it a bit even after close (prevent flapping)
        private readonly Dictionary<string, DateTime> _trackedUntilUtc = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan TrackedGrace = TimeSpan.FromMinutes(20);
        private volatile string _currentSymbol = "—";

        private readonly RealtimePriceService _price;

        private readonly BinanceHistoryImporter _importer;

        // ===============================
        // STRATEGY SIGNAL QUEUE (PRO)
        // ===============================
        //private readonly Channel<TradeSignal> _signalChannel =
        //    Channel.CreateUnbounded<TradeSignal>(
        //        new UnboundedChannelOptions
        //        {
        //            SingleReader = true,
        //            SingleWriter = false
        //        });
        private readonly Channel<TradeSignal> _signalChannel =
    Channel.CreateBounded<TradeSignal>(
        new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        public int StartupSubscriptionCap { get; set; } = 8;
        public TradingWorker(
            ILogger<TradingWorker> logger,
            IOptions<TradingOptions> options,
            MarketDataService market,
            MarketDataFacade marketDataFacade,
            StrategyEngine strategy,
            StrategyRouter strategyRouter,
            RiskManager risk,
            OrderExecutor executor,
            BybitOrderExecutor bybitExecutor,
            ExchangeExecutionRouter exchangeRouter,
            BinanceClientFactory factory,
            LiquidityGuardService liq,
            OrderCleanerService cleaner,
            PredictiveEngineV4ConfirmationService predict,
            AiStopLossOptimizer slOpt,
            AiRiskScalerV2 riskScaler,
            PositionSupervisorService supervisor,
            AiSelfLearningService learn,
            AiModelSnapshotService snapshot,
            SymbolRegistryService symbols,
            AiTimeframeSelectorService tfSelector,
            EngineStateBuilder engineStateBuilder,
            EngineStateSnapshotService engineStateSnapshot,
            IBootGate bootGate,
            TradingOptionsResolver resolver,
            IStrategyPreFilter pre, MarketContextService marketContext, SimulatedTradeService sim,
            LiveSignalService liveSig, AiMarketRegimeService marketRegime, BinanceHistoryImporter importer
            , RealtimePriceService price, SymbolInfoService symbolInfo,
            FundingRateService fundingRate,
            RealtimeMomentumDetector momentum,
            IOptionsMonitor<VertexAutoTradeBinance8.Configuration.SignalConfidenceSettings> confSettings,
            VertexAutoTradeBinance8.Services.HistoricalData.DataDbSymbolFeed? dataDbFeed = null,
            TradeStateManager tradeState = null)
        {
            _logger = logger;
            _options = options.Value;

            _market = market;
            _marketDataFacade = marketDataFacade;
            _strategy = strategy;
            _strategyRouter = strategyRouter;
            _risk = risk;
            _executor   = executor;
            _bybitExecutor = bybitExecutor;
            _exchangeRouter = exchangeRouter;
            _dataDbFeed = dataDbFeed;
            _tradeState = tradeState ?? new TradeStateManager();
            _factory = factory;
            _liq = liq;
            _cleaner = cleaner;
            _predict = predict;
            _slOpt = slOpt;
            _riskScaler = riskScaler;
            _supervisor = supervisor;
            _learn = learn;
            _snapshot = snapshot;
            _symbols = symbols;
            _tfSelector = tfSelector;
            _engineStateBuilder = engineStateBuilder;
            _engineStateSnapshot = engineStateSnapshot;
            _bootGate = bootGate;
            _pre = pre;
            _marketContext = marketContext;
            _fundingRate = fundingRate;
            _momentum    = momentum;

            learn.ForceSnapshot();
            _sim = sim;
            _liveSig = liveSig;
            _marketRegime = marketRegime;
            _importer = importer;
            _price = price;
            _resolver = resolver;
            _symbolInfo = symbolInfo;
            _confSettings = confSettings;
        }

        private int _lastCyclesPerMinute = 0;
        private DateTime _lastUniverseRefreshUtc = DateTime.MinValue;
        private static readonly TimeSpan UniverseRefreshPeriod = TimeSpan.FromSeconds(30);
        private readonly SemaphoreSlim _positionsScanLock = new(1, 1);

        private async Task RejectAsync(
       TradeSignal? signal,
       string symbol,
       KlineInterval tf,
       string stage,
       string reason,
       CancellationToken ct,
       int? sleepMs = null,
       string? extra = null)
        {
            _logger.LogWarning(
                "[PROC][{symbol}][{tf}] REJECT stage={stage} reason={reason}{extra}",
                symbol, tf, stage, reason,
                extra != null ? $" | {extra}" : string.Empty
            );

            if (signal != null)
            {
                // 1️⃣ ВСЕГДА пишем lifecycle (чистый факт)
                try
                {
                    await _sim.AppendLifecycleEventAsync(
                        signal,
                        stage: $"REJECT_{stage}",
                        reason: reason
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SIM] AppendLifecycleEventAsync failed");
                }

                // 2️⃣ MISSED TRADE — ТОЛЬКО если это реально вход
                //    (а не prefilter / технический reject)
                bool isEntryRelated =
                    stage is "ENTRY" or "EXEC" or "RISK";

                if (isEntryRelated)
                {
                    try
                    {
                        await _sim.SimulateMissedTradeAsync(
                            signal,
                            $"{stage}:{reason}",
                            note: extra
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[SIM] SimulateMissedTradeAsync failed");
                    }
                }
            }

            if (sleepMs.HasValue && sleepMs.Value > 0)
                await Task.Delay(sleepMs.Value, ct);
        }


        private async Task WarmupHtfAsync(
           IReadOnlyList<string> symbols,
           CancellationToken ct)
        {
            foreach (var s in symbols)
            {
                await _marketDataFacade.GetKlinesAsync(s, KlineInterval.OneHour, 60, ct);
                await _marketDataFacade.GetKlinesAsync(s, KlineInterval.FourHour, 60, ct);
                await _marketDataFacade.GetKlinesAsync(s, KlineInterval.OneDay, 60, ct);
                await _marketDataFacade.GetKlinesAsync(s, KlineInterval.OneHour, 60, ct);

                _logger.LogInformation("[MD][HTF] warmup {symbol} 1H/4H/1D/1H", s);
            }
        }

        private async Task<List<BinanceFuturesUsdtUserTrade>> LoadFromBinanceAsync(
            DateTime fromUtc, CancellationToken ct)
        {
            var client = _factory.CreateRestClient();
            var allTrades = new List<BinanceFuturesUsdtUserTrade>();
            var retryDelay = TimeSpan.FromSeconds(3);
            const int maxRetries = 5;
            var symbols = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
            WebCallResult<BinanceFuturesUsdtTrade[]> res = null!;

            foreach (var symbol in symbols.Data.Symbols.Where(s => s.QuoteAsset == "USDT" && s.Status == SymbolStatus.Trading &&
    s.ContractType == ContractType.Perpetual))
            {
                long? fromId = null;
                bool finished = false;

                while (!finished)
                {
                    ct.ThrowIfCancellationRequested();

                    int attempt = 0;


                    // ===== RETRY LOOP =====
                    while (attempt < maxRetries)
                    {
                        attempt++;
                        try
                        {
                            res = await client.UsdFuturesApi.Trading.GetUserTradesAsync(
                          symbol: symbol.Pair,
                          startTime: fromId == null ? fromUtc : null,
                          fromId: fromId,
                          limit: 1000,
                          ct: ct
                      );

                            if (res.Success)
                                break;

                            _logger.LogWarning(
                                "[BINANCE][{symbol}] Attempt {attempt}/{maxRetries} failed: {msg}",
                                symbol, attempt, maxRetries, res.Error
                            );

                            // Unauthorized / invalid API key → skip symbol
                            if (res.Error?.Code == -2015 || res.Error?.Code == -2014)
                            {
                                _logger.LogError(
                                    "[BINANCE][{symbol}] Unauthorized / invalid API key. Skipping symbol.",
                                    symbol
                                );
                                finished = true;
                                break;
                            }

                            await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "[BINANCE][{symbol}] Attempt {attempt}/{maxRetries} exception",
                                symbol, attempt, maxRetries
                            );
                            await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                        }
                    }

                    if (res == null || !res.Success || finished)
                        break;

                    if (res.Data == null || res.Data.Length == 0)
                        break;

                    // ===== ADD TO LIST =====
                    allTrades.AddRange(res.Data.Select(t => new BinanceFuturesUsdtUserTrade
                    {
                        Id = t.Id,
                        Symbol = t.Symbol,
                        PositionSide = t.PositionSide,
                        Price = t.Price,
                        Quantity = t.Quantity,
                        Time = t.Timestamp,
                        Buyer = t.Buyer,
                        RealizedPnl = t.RealizedPnl
                    }));

                    // ===== PAGINATION =====
                    fromId = res.Data.Max(t => t.Id) + 1;
                    if (res.Data.Length < 1000)
                    { finished = true; break; }

                }

            }

            return allTrades.GroupBy(t => t.Id)
    .Select(g => g.First())
    .OrderBy(t => t.Time)
    .ToList();
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _logger.LogWarning("[WORKER] Waiting BootGate ...");
            await _bootGate.WaitReadyAsync(ct);
            _logger.LogWarning("[WORKER] BootGate READY");


            // ============================================
            // 0️⃣ IMPORT BINANCE FUTURES HISTORY (AI)
            // ============================================
            try
            {
                var from = _learn.LastImportedTradeCloseUtc ?? DateTime.UtcNow.AddDays(-90);
                _logger.LogInformation("[AI-BOOT] Importing Binance Futures history from {From}", from);

                var fills = await LoadFromBinanceAsync(from.AddMilliseconds(1), ct);
                _importer.ImportClosedTrades(fills);

                _learn.ForceSnapshot();

                _logger.LogInformation("[AI-BOOT] Import done → trades={Count}", _learn.TotalTrades);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI-BOOT] Failed to import Binance Futures history");
            }



            // 1) LOAD SYMBOLS (universe)
            if (DateTime.UtcNow - _lastUniverseRefreshUtc > UniverseRefreshPeriod)
            {
                await _symbols.LoadAsync(ct);
                _lastUniverseRefreshUtc = DateTime.UtcNow;
            }

            // 2) APPLY UNIVERSE → WS subscriptions (1m/5m/15m)
            _marketDataFacade.ApplyUniverse(_symbols.ActiveSymbols);

            // Подключаем RealtimeMomentumDetector к WS
            _marketDataFacade.SetMomentumDetector(_momentum);

            // При обнаружении импульса — немедленно запускаем анализ символа
            _momentum.MomentumDetected += async (symbol, sig) =>
            {
                try
                {
                    _logger.LogInformation(
                        "[MOMENTUM→STRATEGY] {symbol} {dir} strength={str:F2}×ATR — triggering immediate analysis",
                        symbol, sig.IsLong ? "LONG" : "SHORT", sig.Strength);

                    var tf = KlineInterval.FiveMinutes;
                    var ctx = await _marketContext.GetContextAsync(symbol, CancellationToken.None);

                    // Запускаем анализ только для стороны импульса
                    var side = sig.IsLong ? SignalSide.Buy : SignalSide.Sell;
                    if (ctx.Allows(side, 0m))
                        await ProcessSymbolWithUniverseSide(symbol, tf, side, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MOMENTUM→STRATEGY] Error processing {symbol}", symbol);
                }
            };

            // Подписываем FundingRateService на активные символы
            _ = _fundingRate.TrackSymbolsAsync(_symbols.ActiveSymbols);

            _symbols.UniverseChanged += syms =>
            {
                _marketDataFacade.ApplyUniverse(syms);
                // Обновляем символы для funding rate при изменении вселенной
                _ = _fundingRate.TrackSymbolsAsync(syms);
            };

            // 3) RESTORE SNAPSHOT (authoritative if exists)
            try
            {
                await _marketDataFacade.RestoreSnapshotStateAsync(ct);
                // ❗ НЕ ВЫЗЫВАТЬ MarkSnapshotReady() вручную
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WORKER] Market snapshot restore failed");
            }

            // 4) BIND STRATEGY AFTER MARKETDATA READY
            //    v9: StrategyEngine is no longer bound/subscribed directly.
            //    StrategyRouter now owns binding BOTH the trend-following
            //    StrategyEngine and the new MeanReversionEngine to live
            //    market data, and decides per-signal which one reaches the
            //    channel below (regime-based auto routing, or a manual
            //    override toggle — see StrategyModeState). StrategyEngine's
            //    own internal logic is completely unchanged; only WHO
            //    subscribes to its signal stream changed.
            _strategyRouter.BindAll();

            // 🔥 ROUTER → WORKER (PUSH-ONLY)
            _strategyRouter.OnSignalGenerated += signal =>
            {
                if (signal == null) return;
                _signalChannel.Writer.TryWrite(signal);
                _dataDbFeed?.NotifySignal(signal.Symbol);
            };

            // 5) TRACK SYMBOLS (runtime set)
            var startupCap = _options.StartupSubscriptionCap > 0
                ? _options.StartupSubscriptionCap
                : 8;

            // 5a) OPEN POSITIONS FIRST — chart history before universe
            //     (user already has risk on these symbols; empty chart is unacceptable)
            try
            {
                await WarmupOpenPositionsFirstAsync(ct, forceScan: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WORKER] priority position candle warmup failed");
            }

            foreach (var s in _symbols.ActiveSymbols.Take(startupCap))
                TrackSymbol(s, keepAlive: true);

            // 6) HTF WARMUP — positions already pulled; universe HTF next
            //    Put position symbols at the front of the HTF list as well.
            var htfList = (_cachedPositionSymbols ?? Array.Empty<string>())
                .Concat(_symbols.ActiveSymbols)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            await WarmupHtfAsync(htfList, ct);

            // 7) WARMUP LTF — again prioritizes any remaining position gaps
            await WarmupMarketDataForTrackedAsync(ct);

            // 8) ENABLE HEDGE
            await EnableHedgeMode();

            // 9) LOAD AI SNAPSHOT
            try
            {
                var snap = await _snapshot.LoadLatestAsync(ct);
                if (snap != null)
                    _learn.ImportState(snap);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WORKER] AI snapshot import failed");
            }

            // =======================================================
            // 🔒 INIT BASE DEPOSIT (ONCE, HARD ANCHOR)
            // =======================================================
            // _engineStateSnapshot.EnsureDepositInitialized(_options.Deposit);
            var realBalance = await TryGetRealBalanceSafeAsync(ct);

            decimal depositForCalc = realBalance > 0
            ? realBalance
            : _options.Deposit;

            // инициализация состояния движка (однократно)
            _engineStateSnapshot.EnsureDepositInitialized(depositForCalc);

            _logger.LogInformation(
    "[TRADING INIT] Deposit initialized → RealBalance={realBalance}, UsedDeposit={depositForCalc}",
    realBalance, depositForCalc);

            _logger.LogWarning("TradingWorker QUANT-REALTIME STARTED");

            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;

                // ===== ENGINE HEARTBEAT =====
                _lastEngineTickUtc = now;
                _cycleCounter++;

                if ((now - _cycleWindowUtc).TotalSeconds >= 60)
                {
                    _lastCyclesPerMinute = _cycleCounter;
                    _cycleCounter = 0;
                    _cycleWindowUtc = now;
                }

                IReadOnlyList<string> trackedSymbols;

                try
                {
                    await RunQuantRealtimeTick(ct);
                    if (DateTime.UtcNow - _lastUniverseRefreshUtc > UniverseRefreshPeriod)
                    {
                        await _symbols.LoadAsync(ct);
                        _lastUniverseRefreshUtc = DateTime.UtcNow;
                    }
                    trackedSymbols = await BuildTrackedSymbolsAsync(ct);
                    await WarmupMarketDataForTrackedAsync(ct);
                }
                catch
                {
                    await Task.Delay(200, ct);
                    continue;
                }

                // ===========================================
                // STRATEGY SIGNAL CONSUMER (PRO)
                // ===========================================
                while (_signalChannel.Reader.TryRead(out var signal))
                {
                    try
                    {
                        await HandleStrategySignalAsync(signal, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "[WORKER][SIGNAL] fatal handling {symbol}",
                            signal.Symbol);
                    }
                }

                // ===== ROTATION: ПО КАЖДОМУ СИМВОЛУ =====
                foreach (var symbol in trackedSymbols)
                {
                    _currentSymbol = symbol;

                    var selectedTf = await ResolveTimeframeSafeAsync(symbol, ct);  // можно для аналитики/логов
                    var tradeTf = KlineInterval.FiveMinutes; // жестко   // либо маппинг из _options.TimeframeMinutes -> KlineInterval


                    var ctx = await _marketContext.GetContextAsync(symbol, ct);

                    var state = _engineStateBuilder.Build(
                        symbol: symbol,
                        timeframe: selectedTf
                    );

                    state.LastEngineTick = DateTime.UtcNow;
                    state.LastUpdate = state.LastEngineTick;
                    state.CyclesPerMinute = _lastCyclesPerMinute;
                    state.UniverseSize = _symbols.ActiveSymbols.Count;
                    state.TrackedSymbols = trackedSymbols.Count;
                    state.Timeframe = selectedTf;
                    state.OpenPositions = await _supervisor.GetActivePositionsCountAsync(ct);
                    state.BalanceUsdt = await TryGetRealBalanceSafeAsync(ct);

                    _engineStateSnapshot.Save(state);

                    if (ctx.Allows(SignalSide.Buy, 0m))
                        await ProcessSymbolWithUniverseSide(symbol, tradeTf, SignalSide.Buy, ct);

                    if (ctx.Allows(SignalSide.Sell, 0m))
                        await ProcessSymbolWithUniverseSide(symbol, tradeTf, SignalSide.Sell, ct);

                    ConsoleSymbolTableFormatter.UpdateTf(symbol, tradeTf, $"TF={tradeTf} (sel={selectedTf})", "...");


                    await _supervisor.SuperviseAsync(symbol, null, ct);

                    // КОРОТКАЯ ПАУЗА ДЛЯ UI-КАРУСЕЛИ
                    await Task.Delay(25, ct);
                }

                await PeriodicSnapshot(ct);
                await Task.Delay(80, ct);
            }
        }

        public async Task<decimal> TryGetRealBalanceSafeAsync(CancellationToken ct)
        {
            try
            {
                _logger.LogInformation("[BALANCE] Requesting USDT Futures account info...");

                // =====================================================
                // Используем отдельный timeout токен (15 сек) вместо
                // основного ct воркера — иначе при перезапуске/отмене
                // запрос падает с TaskCanceledException
                // =====================================================
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                var acc = await _factory
                    .CreateRestClient()
                    .UsdFuturesApi
                    .Account
                    .GetAccountInfoV3Async(ct: timeoutCts.Token);

                if (!acc.Success)
                {
                    _logger.LogWarning(
                        "[BALANCE] Request failed. Error: {Error}",
                        acc.Error?.Message ?? "unknown");
                    return 0m;
                }

                if (acc.Data == null)
                {
                    _logger.LogWarning("[BALANCE] Response success but Data is NULL.");
                    return 0m;
                }

                var wallet = acc.Data.TotalWalletBalance;
                var available = acc.Data.AvailableBalance;
                var unrealized = acc.Data.TotalUnrealizedProfit;

                _logger.LogInformation(
                    "[BALANCE] Wallet={Wallet} Available={Available} UnrealizedPnL={Unrealized}",
                    wallet, available, unrealized);

                return wallet;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout нашего собственного CTS — сеть медленная, но не критично
                _logger.LogWarning("[BALANCE] Timeout (15s) requesting balance — will retry next cycle");
                return 0m;
            }
            catch (OperationCanceledException)
            {
                // Основной токен отменён — нормальный shutdown
                return 0m;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BALANCE] Exception while requesting balance");
                return 0m;
            }
        }



        private async Task HandleStrategySignalAsync(
        TradeSignal signal,
        CancellationToken ct)
        {
            var symbol = signal.Symbol.Trim().ToUpperInvariant();

            // ★ UI FIRST — even if later guards reject, Market still sees the candidate
            try { await _liveSig.AppendAsync(signal, ct); }
            catch { /* never block */ }

            // ─────────────────────────────────────────────────────────────────────
            // GUARD: Side must be Buy or Sell — never None.
            // Decision trace showed 135 Allow=true records with Side=null, meaning
            // signals passed AI confirmation but had no direction → execution would
            // fail silently. Reject early with a clear reason.
            // ─────────────────────────────────────────────────────────────────────
            if (signal.Side != SignalSide.Buy && signal.Side != SignalSide.Sell)
            {
                await RejectAsync(
                    signal, symbol, default,
                    "INVALID_SIGNAL",
                    $"SIDE_NONE:{signal.Side}",
                    ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(signal.Timeframe))
            {
                await RejectAsync(
                    signal,
                    symbol,
                    default,
                    "INVALID_SIGNAL",
                    "TIMEFRAME_NULL",
                    ct);
                return;
            }

            if (!Enum.TryParse<KlineInterval>(
                    signal.Timeframe,
                    ignoreCase: true,
                    out var tf))
            {
                await RejectAsync(
                    signal,
                    symbol,
                    default,
                    "INVALID_SIGNAL",
                    $"TIMEFRAME_INVALID:{signal.Timeframe}",
                    ct);
                return;
            }
            ConsoleSymbolTableFormatter.UpdateTf(
                symbol, tf, "▶ STRAT", signal.Side.ToString());



            // =====================================================
            // 1.5) LIVE SIGNALS UI — write BEFORE AI gate
            // So /market always shows strategy candidates even if AI later blocks.
            // =====================================================
            try { await _liveSig.AppendAsync(signal, ct); }
            catch { /* never block pipeline for UI */ }

            // =====================================================
            // 2) COOLDOWN
            // =====================================================
            if (InCooldown(symbol))
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "COOLDOWN",
                    "COOLDOWN_ACTIVE",
                    ct);
                return;
            }

            // =====================================================
            // 3) AI CONFIRMATION
            // =====================================================
            AiDecision ai;
            try
            {
                var klines = await _marketDataFacade
                    .GetKlinesAsync(symbol, tf, 200, ct)
                    .ConfigureAwait(false);

                ai = _predict.Decide(symbol, tf, klines, signal);
            }
            catch (Exception ex)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "AI",
                    "AI_ERROR",
                    ct,
                    extra: ex.Message);
                return;
            }

            // LIVE = DEMO: only CORE_* may execute. Everything else is UI-only noise.
            bool isCore = signal.Reason != null &&
                          signal.Reason.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase);
            if (!isCore)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "POLICY",
                    "NON_CORE_BLOCKED",
                    ct,
                    extra: "LIVE executes CORE only (same as Parallel DEMO)");
                return;
            }
            if (!ai.Allow)
            {
                _logger.LogInformation(
                    "[PROC][{symbol}] CORE kept despite AI_BLOCK ({reason})",
                    symbol, ai.Reason);
            }
            // =====================================================
            // 3.1) WRITE TO LIVE SIGNALS (AI confirmed, before pipeline blocks)
            // ─────────────────────────────────────────────────────────────────
            // Signal has passed AI (PE4 score >= 0.20) and has Entry/SL/TP.
            // Write it NOW so /market page shows it regardless of what
            // happens in the rest of the pipeline (execution, cooldown etc.)
            // =====================================================
            try { await _liveSig.AppendAsync(signal, ct); }
            catch { /* never block execution for UI write */ }

            // =====================================================
            // 3.2) MinExecute threshold check
            // ─────────────────────────────────────────────────────
            // Signal already written to UI (watch-only signals visible).
            // Now check if confidence is high enough to actually trade.
            // MinDisplay (0.33) was already checked by AI gate above.
            // MinExecute (0.55) is the execution gate — only trade
            // high-quality signals confirmed by structure/S/R.
            // =====================================================
            {
                // MinExecute gate: signal.Confidence is set by PredictiveEngineV4.
                // Signals below MinExecute are written to UI (watch-only) but not traded.
                // LIVE = DEMO gate: conf ≥ 55% (file stores 55, signal is 0..1 or 0..100)
                var confCfg = _confSettings.CurrentValue;
                decimal minExec = symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase)
                                    ? (confCfg.BTC?.MinExecute ?? 0.55m)
                    : symbol.StartsWith("ETH", StringComparison.OrdinalIgnoreCase)
                                    ? (confCfg.ETH?.MinExecute ?? 0.55m)
                                    : confCfg.Default?.MinExecute ?? 0.55m;
                if (minExec < 0.55m) minExec = 0.55m; // never looser than Demo
                double sigConf = (double)(signal.Confidence ?? 0m);
                if (sigConf > 1.5) sigConf /= 100.0; // normalize percent → 0..1
                if (sigConf < (double)minExec)
                {
                    await RejectAsync(signal, symbol, tf, "CONF", "WATCH_ONLY",
                        ct, extra: $"conf={sigConf:F2} < minExec={minExec:F2} — shown in UI only");
                    return;
                }
            }

            // =====================================================
            // 3.5) CONTEXT SIDE CHECK (NOW WITH CONFIDENCE)
            // =====================================================
            var ctx = await _marketContext.GetContextAsync(symbol, ct);
            if (!ctx.Allows(signal.Side, ai.Score))
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "UNIVERSE_SIDE",
                    "SIDE_NOT_ALLOWED",
                    ct);
                return;
            }
            // =====================================================
            // 4) RISK SCALING
            // =====================================================
            var riskMult = _riskScaler.Scale(ai.Grade);
            if (riskMult <= 0)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "RISK",
                    "RISK_MULT_ZERO",
                    ct);
                return;
            }

            // =====================================================
            // 4.5) MAX OPEN POSITIONS (LIVE = DEMO: max 5)
            // =====================================================
            try
            {
                int openN = await _supervisor.GetActivePositionsCountAsync(ct).ConfigureAwait(false);
                const int MaxLivePositions = 5;
                if (openN >= MaxLivePositions)
                {
                    await RejectAsync(
                        signal, symbol, tf,
                        "RISK",
                        $"MAX_POSITIONS:{openN}>={MaxLivePositions}",
                        ct);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PROC] position-count check failed — continuing");
            }

            // =====================================================
            // 5) LIQUIDITY GUARD (FINAL)
            // =====================================================
            LiquidityGuardResult liq;
            try
            {
                var klines = await _marketDataFacade
                    .GetKlinesAsync(symbol, tf, 120, ct)
                    .ConfigureAwait(false);

                liq = _liq.Analyze(
                    symbol, tf, klines,
                    signal.Side,
                    signal.IsSuperSignal);
            }
            catch (Exception ex)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "LIQUIDITY",
                    "LIQ_SERVICE_ERROR",
                    ct,
                    extra: ex.Message);
                return;
            }

            if (liq.Block)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "LIQUIDITY",
                    $"LIQUIDITY_{liq.Reason}",
                    ct);
                return;
            }

            var trading = _resolver.Resolve(symbol);
            var Level = trading.Leverage > 0
                ? trading.Leverage
                : (signal.Leverage ?? 1m);
            //// =====================================================
            //// 6) QTY
            //// =====================================================
            //var qty = await _risk.CalculateSafeQty(
            //        signal,
            //        symbol,
            //        signal.EntryPrice,
            //        signal.StopLoss,
            //        riskMult,
            //        signal.SafetyRiskMultiplier,
            //        Level,
            //        signal.Side,
            //        signal.TakeProfits,
            //        ct).ConfigureAwait(false);

            //    if (qty <= 0)
            //    {
            //        await RejectAsync(
            //            signal, symbol, tf,
            //            "RISK",
            //            "NO_BALANCE_OR_MIN_NOTIONAL",
            //            ct);
            //        return;
            //    }
            // =====================================================
            // 6) QTY — PropDesk version
            // =====================================================

            // Получаем Binance фильтры
            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal step = filters.step > 0 ? filters.step : 0.001m;
            decimal minQty = filters.minQty > 0 ? filters.minQty : step;

            // Считаем динамический minNotional
            decimal minNotional = filters.minNotional > 0 ? filters.minNotional : ((decimal?)trading.MinNotionalGuard ?? 5m);
            if (trading.MinNotional > 0)
                minNotional = Math.Max(minNotional, trading.MinNotional);

            if (trading.MinNotionalGuardPercent > 0)
            {
                decimal guardValue = (decimal?)trading.MinNotionalGuard ?? 0m;
                decimal dynMin = Math.Max(guardValue, _risk.LastBalanceUsdt * trading.MinNotionalGuardPercent);
                minNotional = Math.Max(minNotional, dynMin);
            }

            // Считаем qty через PropDesk engine
            //var qty = _risk.GetPropDeskQty(signal, _risk.LastBalanceUsdt, minNotional, step, minQty, riskMult, trading);


            //if (qty <= 0)
            //{
            //    await RejectAsync(
            //        signal, symbol, tf,
            //        "RISK",
            //        "NO_BALANCE_OR_MIN_NOTIONAL",
            //        ct);
            //    return;
            //}


            var balance = await _risk.GetRealtimeBalanceAsync(ct);

            if (balance <= 0)
            {
                await RejectAsync(signal, symbol, tf, "NO_BALANCE", "Balance is zero", ct);
                return;
            }

            // LIVE = DEMO: same leverage for sizing and exchange
            var leverageParity = RiskManager.GetDemoParityLeverage(symbol);
            var qty = _risk.GetPropDeskQtyFinal(
                signal,
                balance,
                step,
                minQty,
                riskMult,
                trading,
                effectiveLeverage: leverageParity);

            if (qty <= 0)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "RISK",
                    $"QTY_ZERO: {_risk.LastRejectReason}",
                    ct);
                return;
            }
            // =====================================================
            // 7) SL / TP — CORE keeps signal levels (1:1 with DEMO)
            // =====================================================
            try
            {
                // Do NOT rewrite CORE SL/TP via optimizer — DEMO uses signal as-is.
                if (!isCore)
                {
                    var klines = await _marketDataFacade
                        .GetKlinesAsync(symbol, tf, 120, ct)
                        .ConfigureAwait(false);

                    signal.StopLoss =
                        _slOpt.OptimizeSlAndTp(symbol, klines, signal, ai);
                }

                await _cleaner
                    .CleanupOutdatedOrdersAsync(symbol, signal, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "PROTECTION",
                    "PROTECTION_ERROR",
                    ct,
                    extra: ex.Message);
                return;
            }

            // =====================================================
            // 8) EXECUTION
            // =====================================================


            // 🔥 realtime price override
            if (_price.TryGet(symbol, out var realtimePrice) && realtimePrice > 0)
            {
                signal.EntryPrice = realtimePrice;
            }

            var leverage = RiskManager.GetDemoParityLeverage(symbol);
            _logger.LogInformation("[EXEC][{sym}] DEMO-PARITY leverage={lev}x", symbol, leverage);
            _dataDbFeed?.NotifyExecution(signal.Symbol);

            // ── Multi-exchange (phase-2): Binance and/or Bybit ──────────
            _exchangeRouter.LogRouting(signal);
            bool wantBinance = _exchangeRouter.ShouldExecuteOnBinance();
            bool wantBybit   = _exchangeRouter.ShouldExecuteOnBybit();

            OrderResult? binanceResult = null;
            OrderResult? bybitResult = null;

            if (wantBinance)
            {
                binanceResult = await _executor.ExecuteAsync(signal, qty, ct, leverage);
                if (!binanceResult.Success)
                    _logger.LogWarning("[EXEC] Binance failed {sym}: {err}", symbol, binanceResult.Error);
            }

            if (wantBybit)
            {
                try
                {
                    bybitResult = await _bybitExecutor.ExecuteAsync(signal, qty, leverage, ct);
                    if (!bybitResult.Success)
                        _logger.LogWarning("[EXEC] Bybit failed {sym}: {err}", symbol, bybitResult.Error);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[EXEC] Bybit exception {sym}", symbol);
                    bybitResult = new OrderResult { Success = false, Error = ex.Message };
                }
            }

            bool ok =
                (wantBinance && binanceResult?.Success == true)
                || (wantBybit && bybitResult?.Success == true);

            // Nothing enabled → treat as config error
            if (!wantBinance && !wantBybit)
            {
                await RejectAsync(signal, symbol, tf, "EXEC", "NO_EXCHANGE_ENABLED", ct);
                return;
            }

            if (!ok)
            {
                var err = string.Join(" | ",
                    new[]
                    {
                        wantBinance ? $"Binance:{binanceResult?.Error}" : null,
                        wantBybit ? $"Bybit:{bybitResult?.Error}" : null
                    }.Where(x => x != null));
                await RejectAsync(
                    signal, symbol, tf,
                    "EXEC",
                    "EXECUTION_FAILED",
                    ct,
                    extra: err);
                return;
            }

            // =====================================================
            // 9) SUCCESS
            // =====================================================
            MarkTrade(symbol);
            TrackSymbol(symbol, keepAlive: true);

            await _supervisor
                .SuperviseAsync(symbol, signal, ct)
                .ConfigureAwait(false);

            ConsoleSymbolTableFormatter.UpdateTf(
                symbol, tf,
                "🟩 OK",
                $"{signal.Side} qty={qty:F4}");
        }


        // ===========================================
        // TRACKED SYMBOLS CORE
        // ===========================================
        private void TrackSymbol(string symbol, bool keepAlive)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            symbol = symbol.Trim().ToUpperInvariant();

            _tracked.TryAdd(symbol, 0);

            if (keepAlive)
                _trackedUntilUtc[symbol] = DateTime.UtcNow.Add(TrackedGrace);
        }

        private async Task<IReadOnlyList<string>> BuildTrackedSymbolsAsync(CancellationToken ct)
        {
            var pinnedSet = new HashSet<string>(
                _symbols.PinnedSymbols ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            // 1) universe — всегда отслеживаем
            foreach (var s in _symbols.ActiveSymbols)
                TrackSymbol(s, keepAlive: false);

            // 2) позиции — всегда keepAlive
            var posSymbols = await GetPositionSymbolsThrottledAsync(ct);
            foreach (var s in posSymbols)
                TrackSymbol(s, keepAlive: true);

            var now = DateTime.UtcNow;

            var posSet = new HashSet<string>(posSymbols, StringComparer.OrdinalIgnoreCase);
            var uniSet = new HashSet<string>(_symbols.ActiveSymbols, StringComparer.OrdinalIgnoreCase);

            foreach (var sym in _tracked.Keys.ToArray())
            {
                if (uniSet.Contains(sym))
                    continue;

                if (posSet.Contains(sym))
                    continue;

                if (pinnedSet.Contains(sym))
                    continue;

                if (_trackedUntilUtc.TryGetValue(sym, out var until) && until > now)
                    continue;

                // DROP
                _tracked.TryRemove(sym, out _);
                _trackedUntilUtc.Remove(sym);

                _logger.LogInformation(
                    "[TRACKED] drop {sym} (not in universe, no position, not pinned, grace expired)",
                    sym);
            }

            var list = _tracked.Keys
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.LogInformation(
                "[TRACKED] total={tot} universe={uni} positions={pos}",
                list.Count,
                _symbols.ActiveSymbols.Count,
                posSymbols.Count);

            return list;
        }

        private async Task<IReadOnlyList<string>> GetPositionSymbolsThrottledAsync(
            CancellationToken ct, bool forceRefresh = false)
        {
            if (!forceRefresh &&
                (DateTime.UtcNow - _lastPositionsScanUtc) < TimeSpan.FromSeconds(30))
                return _cachedPositionSymbols;

            await _positionsScanLock.WaitAsync(ct);
            try
            {
                if (!forceRefresh &&
                    (DateTime.UtcNow - _lastPositionsScanUtc) < TimeSpan.FromSeconds(30))
                    return _cachedPositionSymbols;

                _lastPositionsScanUtc = DateTime.UtcNow;

                using var client = _factory.CreateRestClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: timeoutCts.Token);
                if (!res.Success || res.Data == null)
                    return _cachedPositionSymbols;

                var symbols = res.Data
                    .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Symbol) && p.Quantity != 0m)
                    .Select(p => p.Symbol.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _cachedPositionSymbols = symbols;
                return symbols;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[TRACKED] position scan failed");
                return _cachedPositionSymbols;
            }
            finally
            {
                _positionsScanLock.Release();
            }
        }

        /// <summary>
        /// Pull full multi-TF history for every open position symbol FIRST.
        /// Chart/Web must not wait behind universe warmup when a live position
        /// has no candle buffer yet.
        /// </summary>
        private async Task WarmupOpenPositionsFirstAsync(CancellationToken ct, bool forceScan = true)
        {
            IReadOnlyList<string> posSymbols;
            try
            {
                posSymbols = await GetPositionSymbolsThrottledAsync(ct, forceRefresh: forceScan)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BOOT][MD] position scan for priority warmup failed");
                return;
            }

            if (posSymbols == null || posSymbols.Count == 0)
            {
                _logger.LogInformation("[BOOT][MD] no open positions — skip priority candle pull");
                return;
            }

            _logger.LogInformation(
                "[BOOT][MD] PRIORITY candle history for {n} open position(s): {syms}",
                posSymbols.Count, string.Join(",", posSymbols));

            foreach (var raw in posSymbols)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var s = raw.Trim().ToUpperInvariant();
                TrackSymbol(s, keepAlive: true);

                // Already fully warmed this process lifetime
                if (_warm.ContainsKey(s)) continue;

                try
                {
                    _logger.LogInformation("[BOOT][MD] POSITION-FIRST bootstrap {sym}", s);
                    // Deep history so Market chart is never empty for live risk
                    await _marketDataFacade.GetKlinesAsync(s, KlineInterval.OneMinute,      300, ct);
                    await _marketDataFacade.GetKlinesAsync(s, KlineInterval.FiveMinutes,    300, ct);
                    await _marketDataFacade.GetKlinesAsync(s, KlineInterval.FifteenMinutes, 300, ct);
                    await _marketDataFacade.GetKlinesAsync(s, KlineInterval.ThirtyMinutes,  200, ct);
                    await _marketDataFacade.GetKlinesAsync(s, KlineInterval.OneHour,        200, ct);
                    await _marketDataFacade.GetKlinesAsync(s, KlineInterval.FourHour,       120, ct);

                    var extended = _symbols.ActiveSymbols
                        .Append(s)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    _marketDataFacade.ApplyUniverse(extended);
                    _dataDbFeed?.NotifyPosition(s);

                    _warm.TryAdd(s, 0);
                    _logger.LogInformation("[BOOT][MD] POSITION-FIRST ok {sym}", s);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BOOT][MD] POSITION-FIRST failed {sym}", s);
                }
            }
        }

        private async Task WarmupMarketDataForTrackedAsync(CancellationToken ct)
        {
            // ── 0. ALWAYS prioritize open positions (missing chart history) ──
            await WarmupOpenPositionsFirstAsync(ct, forceScan: false).ConfigureAwait(false);

            var posSet = new HashSet<string>(
                (_cachedPositionSymbols ?? Array.Empty<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim().ToUpperInvariant()),
                StringComparer.OrdinalIgnoreCase);

            // ── 1. Any remaining position symbols still not warm (safety net) ──
            var positionQueue = posSet
                .Except(_warm.Keys, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            // ── 2. Universe / tracked symbols AFTER positions ───────────────
            var universeQueue = _tracked.Keys
                .Except(_warm.Keys, StringComparer.OrdinalIgnoreCase)
                .Except(posSet, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            var allToWarm = positionQueue.Concat(universeQueue).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (allToWarm.Count == 0) return;

            foreach (var s in allToWarm)
            {
                bool isPosition = posSet.Contains(s);
                try
                {
                    if (isPosition)
                    {
                        _logger.LogInformation("[BOOT][MD] position symbol warmup {sym}", s);
                        await _marketDataFacade.GetKlinesAsync(s, KlineInterval.OneMinute,      300, ct);
                        await _marketDataFacade.GetKlinesAsync(s, KlineInterval.FiveMinutes,    300, ct);
                        await _marketDataFacade.GetKlinesAsync(s, KlineInterval.FifteenMinutes, 300, ct);
                        await _marketDataFacade.GetKlinesAsync(s, KlineInterval.OneHour,        200, ct);
                        var extended = _symbols.ActiveSymbols
                            .Append(s)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        _marketDataFacade.ApplyUniverse(extended);
                        _dataDbFeed?.NotifyPosition(s);
                    }
                    else
                    {
                        await _marketDataFacade.GetKlinesAsync(s, KlineInterval.OneMinute,      20, ct);
                        await _marketDataFacade.GetKlinesAsync(s, KlineInterval.FiveMinutes,    20, ct);
                        await _marketDataFacade.GetKlinesAsync(s, KlineInterval.FifteenMinutes, 20, ct);
                    }

                    _warm.TryAdd(s, 0);
                    _logger.LogInformation("[BOOT][MD] warmup ok {sym} (position={p})", s, isPosition);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[BOOT][MD] warmup failed {sym}", s);
                }
            }
        }

        // ===========================================
        // Existing logic (kept)
        // ===========================================

        private async Task<KlineInterval> ResolveTimeframeSafeAsync(string symbol, CancellationToken ct)
        {
            // =====================================================
            // SCALPING MODE: приоритет 5M/1M
            // Fallback = 5M (не 15M как раньше)
            // =====================================================
            var fallback = KlineInterval.FiveMinutes;

            try
            {
                var m1Task = _market.GetMarketSnapshot(symbol, KlineInterval.OneMinute, ct);
                var m5Task = _market.GetMarketSnapshot(symbol, KlineInterval.FiveMinutes, ct);

                var m1 = await m1Task;
                var m5 = await m5Task;

                if (m1 != null && m5 != null)
                {
                    var d = _tfSelector.SelectTF(m1, m5, m5); // m15 = m5 (игнор 15M)
                    return d switch
                    {
                        DominantTF.OneMinute    => KlineInterval.OneMinute,
                        DominantTF.FiveMinutes  => KlineInterval.FiveMinutes,
                        _                       => KlineInterval.FiveMinutes
                    };
                }
            }
            catch { }

            return fallback;
        }

        private async Task ProcessSymbolWithUniverseSide(string symbol, KlineInterval tf, SignalSide desiredSide, CancellationToken ct)
        {
            symbol = symbol.Trim().ToUpperInvariant();

            bool allowed =
                desiredSide == SignalSide.Buy
                    ? _symbols.ActiveLongSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase)
                    : _symbols.ActiveShortSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase);

            if (!allowed)
            {
                await RejectAsync(
                    signal: null,
                    symbol: symbol,
                    tf: tf,
                    stage: "UNIVERSE_SIDE",
                    reason: desiredSide == SignalSide.Buy ? "NOT_IN_ACTIVE_LONG" : "NOT_IN_ACTIVE_SHORT",
                    ct: ct);

                return;
            }

            await ProcessSymbol(symbol, tf, desiredSide, ct);
        }

        private async Task ProcessSymbol(
    string symbol,
    KlineInterval tf,
    SignalSide desiredSide,
    CancellationToken ct)
        {
            // ❗❗❗
            // ENTRY PATH DISABLED
            // StrategyEngine is the ONLY signal source.
            // This method is kept ONLY to avoid breaking legacy call sites.
            // ❗❗❗

            _logger.LogDebug(
                "[PROC][{symbol}][{tf}] ProcessSymbol skipped — entry handled by StrategyEngine",
                symbol, tf);

            // НИЧЕГО НЕ ДЕЛАЕМ
            // ❌ никаких сигналов
            // ❌ никакого AI
            // ❌ никакого execution

            await Task.CompletedTask;
        }


        // Cooldown check using per-symbol resolved options.
        // Previously used _options.CooldownMinutes which reads from the
        // shared TradingOptions default; the Trading section in appsettings
        // has no CooldownMinutes key so the field was always 0, making
        // InCooldown permanently return false — effectively disabling the
        // cooldown gate entirely for every symbol.
        // Now uses _resolver.Resolve(symbol).CooldownSeconds so BTC/ETH
        // get their per-symbol 120s and everything else gets the 90s default.
        private bool InCooldown(string symbol)
        {
            var trading = _resolver.Resolve(symbol);
            int cooldownSec = trading.CooldownSeconds > 0
                ? trading.CooldownSeconds
                : (_options.CooldownSeconds > 0 ? _options.CooldownSeconds : 180);

            // ── TradeStateManager checks (shared with PositionSupervisor) ──
            // 3 consecutive SL hits → block this symbol until a win
            if (_tradeState.IsLosingStreakLimit(symbol, 5))
                return true;

            // After any SL: 2× cooldown from TradeStateManager
            int penaltyMin = Math.Max(1, cooldownSec * 2 / 60);
            if (_tradeState.IsInCooldown(symbol, penaltyMin))
                return true;

            // ── Local fallback ──
            if (_slPenalty.TryGetValue(symbol, out var slTime))
            {
                var penaltySec = cooldownSec * 2;
                if (DateTime.UtcNow - slTime < TimeSpan.FromSeconds(penaltySec))
                    return true;
                _slPenalty.TryRemove(symbol, out _);
            }

            return _lastTrade.TryGetValue(symbol, out var last)
                   && DateTime.UtcNow - last < TimeSpan.FromSeconds(cooldownSec);
        }

        private void MarkTrade(string symbol) =>
            _lastTrade[symbol] = DateTime.UtcNow;

        // After a SL hit, double the cooldown for that symbol to prevent
        // re-entering the same trap. The penalty decays: if the NEXT trade
        // is a winner, normal cooldown resumes.
        private readonly ConcurrentDictionary<string, DateTime> _slPenalty = new();

        /// <summary>Call when a position is closed by stop loss.</summary>
        public void MarkStopLossHit(string symbol)
        {
            _slPenalty[symbol] = DateTime.UtcNow;
            // Also push _lastTrade forward so InCooldown picks it up
            _lastTrade[symbol] = DateTime.UtcNow;
            _logger.LogWarning("[COOLDOWN] SL hit on {symbol} — 2× cooldown penalty active", symbol);
        }

        /// <summary>Call when a position is closed by TP (winner).</summary>
        public void ClearStopLossPenalty(string symbol)
        {
            _slPenalty.TryRemove(symbol, out _);
        }

        private async Task RunQuantRealtimeTick(CancellationToken ct)
        {
            if ((DateTime.UtcNow - _lastQuantTick).TotalSeconds < 60)
                return;

            _lastQuantTick = DateTime.UtcNow;

            try
            {
                await _snapshot.SaveSnapshotAsync(_learn.ExportState(), ct);
            }
            catch { }
        }
        private async Task PeriodicSnapshot(CancellationToken ct)
        {
            try
            {
                if (DateTime.UtcNow.Second < 2)
                    await _snapshot.SaveSnapshotAsync(_learn.ExportState(), ct);
            }
            catch { }
        }

        private async Task EnableHedgeMode()
        {
            using var client = _factory.CreateRestClient();

            var current = await client.UsdFuturesApi.Account.GetPositionModeAsync();
            if (current.Success && current.Data?.IsHedgeMode == true)
                return;

            await client.UsdFuturesApi.Account.ModifyPositionModeAsync(true);
        }
    }
}



