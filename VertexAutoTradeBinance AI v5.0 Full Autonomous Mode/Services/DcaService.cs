using System.Text.Json;
using Binance.Net.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Services.MarketData;

namespace VertexAutoTradeBinance8.Services
{
    public sealed class DcaPurchaseRecord
    {
        public string Symbol { get; set; } = "";
        public DateTime TimeUtc { get; set; }
        public decimal Price { get; set; }
        public decimal Qty { get; set; }
        public decimal UsdtSpent { get; set; }
        public bool DipBonusApplied { get; set; }
    }

    public sealed class DcaState
    {
        // Last time a scheduled cycle actually fired (not per-symbol —
        // the whole cycle, all configured symbols, happens together on
        // the same schedule tick).
        public DateTime LastCycleUtc { get; set; }
        public List<DcaPurchaseRecord> History { get; set; } = new();
    }

    /// <summary>
    /// Runs the DCA strategy on a fixed schedule, fully independent of
    /// StrategyEngine's signal-reactive logic — per direct confirmation,
    /// this is a deliberately separate, classical Dollar-Cost Averaging
    /// strategy (buys on schedule regardless of market conditions),
    /// with only one explicit, auditable market-awareness override
    /// (DipBonus) rather than indicator-driven entry timing.
    /// </summary>
    public sealed class DcaService : BackgroundService
    {
        private readonly ILogger<DcaService> _logger;
        private readonly IOptionsMonitor<DcaOptions> _options;
        private readonly BinanceClientFactory _clientFactory;
        private readonly MarketDataFacade _marketData;
        private readonly string _statePath;
        private readonly object _lock = new();
        private DcaState _state = new();

        // Checking once per hour is plenty — schedules here are
        // measured in days/weeks, not minutes, so there's no need for
        // tighter polling.
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

        public DcaService(
            ILogger<DcaService> logger,
            IOptionsMonitor<DcaOptions> options,
            BinanceClientFactory clientFactory,
            MarketDataFacade marketData,
            IConfiguration configuration)
        {
            _logger = logger;
            _options = options;
            _clientFactory = clientFactory;
            _marketData = marketData;

            // SharedData:Root, not AppContext.BaseDirectory — this file
            // needs to be readable cross-process by the separate Web
            // app (Settings page's recent-purchases display), matching
            // DemoAccountService's own established convention for
            // exactly this kind of cross-process file.
            var sharedRoot = configuration["SharedData:Root"];
            var baseDir = !string.IsNullOrWhiteSpace(sharedRoot) ? sharedRoot : AppContext.BaseDirectory;
            _statePath = Path.Combine(baseDir, "dca_state.json");
            Load();
        }

        public DcaState GetSnapshot()
        {
            lock (_lock)
            {
                return new DcaState
                {
                    LastCycleUtc = _state.LastCycleUtc,
                    History = _state.History.ToList(),
                };
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Run an initial check shortly after startup (in case the
            // process was down when a scheduled cycle should have
            // fired), then settle into the hourly check loop.
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAndRunCycleAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[DCA] Cycle check failed");
                }

                try { await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }
            }
        }

        private async Task CheckAndRunCycleAsync(CancellationToken ct)
        {
            var opts = _options.CurrentValue;
            if (!opts.Enabled || opts.Symbols.Count == 0) return;

            if (!IsCycleDueNow(opts.Schedule, _state.LastCycleUtc, DateTime.UtcNow)) return;

            _logger.LogInformation("[DCA] Scheduled cycle starting — {count} symbols, budget {budget} USDT",
                opts.Symbols.Count, opts.Schedule.BudgetPerCycle);

            var client = _clientFactory.TryCreateRestClient();
            if (client == null)
            {
                _logger.LogWarning("[DCA] No Binance client available — skipping this cycle, will retry next check");
                return;
            }

            var allocations = ComputeAllocations(opts);

            foreach (var (symbol, usdtAmount) in allocations)
            {
                try
                {
                    await BuyOneSymbolAsync(client, opts, symbol, usdtAmount, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[DCA] Failed to buy {symbol}", symbol);
                }

                // Small pause between symbols in the same cycle, same
                // reasoning as elsewhere in this codebase — avoid
                // hammering the API with back-to-back signed requests.
                await Task.Delay(300, ct).ConfigureAwait(false);
            }

            lock (_lock)
            {
                _state.LastCycleUtc = DateTime.UtcNow;
                Save();
            }
        }

        // Splits the per-cycle budget across symbols per AllocationMode
        // - "Weighted" uses each entry's relative Weight (the standard,
        // professional allocation pattern: e.g. BTC gets the largest
        // share, alts smaller), "Equal" splits evenly regardless of
        // Weight.
        private static List<(string Symbol, decimal UsdtAmount)> ComputeAllocations(DcaOptions opts)
        {
            var result = new List<(string, decimal)>();
            if (opts.Symbols.Count == 0) return result;

            bool weighted = string.Equals(opts.AllocationMode, "Weighted", StringComparison.OrdinalIgnoreCase);
            decimal totalWeight = weighted ? opts.Symbols.Sum(s => Math.Max(0.0001m, s.Weight)) : opts.Symbols.Count;

            foreach (var s in opts.Symbols)
            {
                decimal share = weighted ? Math.Max(0.0001m, s.Weight) / totalWeight : 1m / opts.Symbols.Count;
                result.Add((s.Symbol, opts.Schedule.BudgetPerCycle * share));
            }
            return result;
        }

        private async Task BuyOneSymbolAsync(
            Binance.Net.Clients.BinanceRestClient client, DcaOptions opts, string symbol, decimal usdtAmount, CancellationToken ct)
        {
            var tickerRes = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct).ConfigureAwait(false);
            if (!tickerRes.Success || tickerRes.Data == null)
            {
                _logger.LogWarning("[DCA] Could not fetch price for {symbol}: {error}", symbol, tickerRes.Error?.Message);
                return;
            }
            decimal currentPrice = tickerRes.Data.Price;

            // Clamp leverage to a safe range and sync it with the exchange
            // before calculating position size. Without this, the account's
            // current leverage for the symbol (which could be anything — left
            // over from a manual trade, or the Binance default) silently
            // determined the actual margin used, making the math below wrong.
            int leverage = Math.Clamp(opts.Leverage > 0 ? opts.Leverage : 3, 1, 20);
            try
            {
                // Clamp to the exchange's own max for this symbol (e.g. some
                // low-liquidity alts cap out at 10x even if 20x is configured).
                var brackets = await client.UsdFuturesApi.Account.GetBracketsAsync(symbol, ct: ct).ConfigureAwait(false);
                if (brackets.Success && brackets.Data != null)
                {
                    var sb = brackets.Data.FirstOrDefault(b => b.Symbol == symbol);
                    int exchangeMax = sb?.Brackets?.Length > 0 ? sb.Brackets.Max(b => b.InitialLeverage) : 20;
                    if (leverage > exchangeMax)
                    {
                        _logger.LogWarning("[DCA][{symbol}] Configured leverage {cfg}x exceeds exchange max {max}x — clamping", symbol, leverage, exchangeMax);
                        leverage = exchangeMax;
                    }
                }
                var levRes = await client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage, ct: ct).ConfigureAwait(false);
                if (!levRes.Success)
                    _logger.LogWarning("[DCA][{symbol}] ChangeInitialLeverageAsync failed: {err} — proceeding with account's current setting", symbol, levRes.Error?.Message);
                else
                    _logger.LogInformation("[DCA][{symbol}] Leverage set to {lev}x", symbol, leverage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DCA][{symbol}] Leverage sync failed — proceeding", symbol);
            }

            bool dipBonusApplied = false;
            decimal finalUsdtAmount = usdtAmount;

            if (opts.DipBonus.Enabled)
            {
                decimal? oldPrice = await TryGetPriceHoursAgoAsync(symbol, opts.DipBonus.LookbackHours, ct).ConfigureAwait(false);
                if (oldPrice.HasValue && oldPrice.Value > 0)
                {
                    decimal dropPct = (oldPrice.Value - currentPrice) / oldPrice.Value * 100m;
                    if (dropPct >= opts.DipBonus.DropThresholdPct)
                    {
                        finalUsdtAmount = usdtAmount * opts.DipBonus.Multiplier;
                        dipBonusApplied = true;
                        _logger.LogInformation(
                            "[DCA] Dip bonus triggered for {symbol}: price dropped {drop:F1}% over {hours}h — buying {mult}x ({amount} USDT instead of {base_})",
                            symbol, dropPct, opts.DipBonus.LookbackHours, opts.DipBonus.Multiplier, finalUsdtAmount, usdtAmount);
                    }
                }
            }

            // Round quantity to the symbol's real step size — same
            // reasoning used throughout this codebase: an arbitrary
            // quantity almost never lands on a valid precision on its
            // own, and the exchange rejects it outright if not rounded.
            decimal step = 0.001m;
            try
            {
                var ei = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct).ConfigureAwait(false);
                var symInfo = ei.Success ? ei.Data.Symbols.FirstOrDefault(s => s.Name == symbol) : null;
                if (symInfo?.LotSizeFilter?.StepSize > 0) step = symInfo.LotSizeFilter.StepSize;
            }
            catch { /* fall back to the conservative default step above */ }

            decimal qty = Math.Floor((finalUsdtAmount / currentPrice) / step) * step;
            if (qty <= 0)
            {
                _logger.LogWarning("[DCA] {symbol}: computed quantity rounds to zero (amount {amount} USDT too small for this symbol's step size) — skipping",
                    symbol, finalUsdtAmount);
                return;
            }

            var orderRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: OrderSide.Buy,
                type: FuturesOrderType.Market,
                quantity: qty,
                positionSide: await ResolveLongPositionSideAsync(client, ct).ConfigureAwait(false),
                ct: ct).ConfigureAwait(false);

            if (!orderRes.Success)
            {
                _logger.LogError("[DCA] Order failed for {symbol}: {error}", symbol, orderRes.Error?.Message);
                return;
            }

            lock (_lock)
            {
                _state.History.Insert(0, new DcaPurchaseRecord
                {
                    Symbol = symbol, TimeUtc = DateTime.UtcNow, Price = currentPrice,
                    Qty = qty, UsdtSpent = finalUsdtAmount, DipBonusApplied = dipBonusApplied,
                });
                // Keep the history from growing unbounded — same
                // pruning approach used elsewhere in this codebase.
                if (_state.History.Count > 500) _state.History = _state.History.Take(500).ToList();
            }

            _logger.LogInformation("[DCA] Bought {qty} {symbol} @ {price} ({amount} USDT){dip}",
                qty, symbol, currentPrice, finalUsdtAmount, dipBonusApplied ? " [DIP BONUS]" : "");
        }

        // Resolves the right positionSide for a hedge-mode account (DCA
        // is exclusively a buy/accumulate strategy, so always the Long
        // side there), or null for a one-way account where positionSide
        // must NOT be set at all. Matches OrderExecutor's own detection
        // pattern elsewhere in this codebase.
        private static async Task<PositionSide?> ResolveLongPositionSideAsync(Binance.Net.Clients.BinanceRestClient client, CancellationToken ct)
        {
            try
            {
                var res = await client.UsdFuturesApi.Account.GetPositionModeAsync(ct: ct).ConfigureAwait(false);
                bool isHedge = res.Success && res.Data?.IsHedgeMode == true;
                return isHedge ? PositionSide.Long : null;
            }
            catch
            {
                return null;
            }
        }

        private async Task<decimal?> TryGetPriceHoursAgoAsync(string symbol, int hoursAgo, CancellationToken ct)
        {
            try
            {
                // Hourly klines, asking for enough bars back to cover
                // the lookback window plus a small buffer.
                var klines = await _marketData.GetKlinesAsync(symbol, KlineInterval.OneHour, hoursAgo + 2, ct).ConfigureAwait(false);
                if (klines == null || klines.Count == 0) return null;
                // Closest bar to "hoursAgo hours back" — klines are
                // oldest-first, so index from the end.
                int idx = Math.Max(0, klines.Count - 1 - hoursAgo);
                return klines[idx].ClosePrice;
            }
            catch
            {
                return null;
            }
        }

        // Determines whether a scheduled cycle is due right now, given
        // the last time one fired. Deliberately simple date-matching,
        // not a cron-style engine — DCA schedules here are at most
        // monthly, so this doesn't need to be more sophisticated.
        public static bool IsCycleDueNow(DcaOptions.DcaScheduleOptions schedule, DateTime lastCycleUtc, DateTime nowUtc)
        {
            // Convert to the configured timezone's LOCAL time first -
            // every comparison below (hour, day of week, day of month)
            // runs against that local time, not raw UTC. This matters
            // for more than just the hour: the day of week itself can
            // shift across the UTC/local boundary (11pm UTC Monday can
            // already be Tuesday in an eastern zone, or still Sunday
            // in a western one), so this has to happen before any of
            // the date-matching logic, not just the hour check.
            // .NET's TimeZoneInfo handles DST transitions (EDT/EST,
            // etc) automatically - no custom date math needed.
            DateTime nowLocal;
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(schedule.TimeZoneId) ? "UTC" : schedule.TimeZoneId);
                nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
            }
            catch
            {
                // Unknown/invalid timezone id — fall back to UTC
                // rather than throwing and silently breaking the
                // entire schedule.
                nowLocal = nowUtc;
            }

            // Allow firing AT OR AFTER the target hour, not only in
            // the exact hour itself - if the process wasn't running
            // (or this check simply hasn't run yet) right when the
            // target hour started, this still catches up later the
            // same day instead of silently waiting a full extra day.
            if (nowLocal.Hour < schedule.HourUtc) return false;

            bool dateMatches = schedule.Frequency.ToLowerInvariant() switch
            {
                "daily" => true,
                "weekly" => (int)nowLocal.DayOfWeek == (schedule.DayOfWeek % 7), // .NET DayOfWeek is Sunday=0; schedule uses ISO Monday=1..Sunday=7
                "monthly" => nowLocal.Day == schedule.DayOfMonth,
                _ => false,
            };
            if (!dateMatches) return false;

            // Guard against firing twice on the same day now that the
            // check above is "at or after" rather than an exact hour
            // match - require at least 20 hours since the last cycle.
            // Compared in UTC (lastCycleUtc/nowUtc are both already
            // UTC) since elapsed-time math doesn't need the timezone
            // conversion the date-matching above does.
            return (nowUtc - lastCycleUtc) > TimeSpan.FromHours(20);
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_statePath))
                {
                    var json = File.ReadAllText(_statePath);
                    var loaded = JsonSerializer.Deserialize<DcaState>(json);
                    if (loaded != null) _state = loaded;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DCA] Failed to load dca_state.json — starting fresh");
            }
        }

        private void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
                var tmp = _statePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _statePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DCA] Failed to save dca_state.json");
            }
        }
    }
}
