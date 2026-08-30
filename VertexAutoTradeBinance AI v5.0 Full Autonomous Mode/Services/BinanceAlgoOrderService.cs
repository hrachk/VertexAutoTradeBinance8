using Binance.Net.Enums;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VertexAutoTradeBinance8.Services
{
    public sealed class BinanceAlgoOrderInfo
    {
        public long AlgoId;
        public string? ClientAlgoId;
        public string Symbol = "";
        public OrderSide Side;
        public PositionSide PositionSide;
        public string OrderType = ""; // "STOP" / "TAKE_PROFIT" (per Binance's algo-order naming)
        public decimal TriggerPrice;
        public decimal Quantity;

        public bool IsStop => OrderType.Contains("STOP", StringComparison.OrdinalIgnoreCase);
        public bool IsTakeProfit => OrderType.Contains("TAKE_PROFIT", StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================
    // BinanceAlgoOrderService — extracted from PositionSupervisorService's
    // originally-private BinanceAlgoOrderRaw class so it can be shared
    // as a proper singleton DI service. Binance.Net 12.13.0 doesn't yet
    // expose typed wrappers for the Algo Order endpoints Binance
    // mandated for ALL conditional orders (STOP_MARKET/TAKE_PROFIT_MARKET)
    // since the Dec 9 2025 migration, so this calls them directly via
    // raw signed HTTP — same HMAC-SHA256 approach used everywhere else
    // in this codebase for Binance auth, just targeting a different
    // endpoint family.
    //
    // Before this was made a shared service, ONLY PositionSupervisorService
    // had access to it (as a private nested class) — OrderExecutor had
    // no way to place a real conditional order at all, which meant its
    // own at-entry TP placement (PlaceOrderAsync with
    // FuturesOrderType.TakeProfitMarket, no algo fallback) was silently
    // failing against the new mandatory endpoint every single time,
    // with no fallback path to actually succeed.
    // ============================================================
    public sealed class BinanceAlgoOrderService
    {
        private readonly HttpClient _http;
        private readonly ILogger<BinanceAlgoOrderService> _logger;
        private readonly TradingCredentialStore _creds;
        private readonly string _fallbackApiKey;
        private readonly string _fallbackApiSecret;
        private readonly string _baseUrl;

        // ── Server time sync ──────────────────────────────────────────────
        // Binance rejects requests with code -1021 when the local clock is
        // ahead of (or too far behind) their server time by more than 1000ms.
        // We fix this by fetching Binance server time once at startup (and
        // periodically thereafter) and computing a _timeOffsetMs so that
        // every signed timestamp we send is aligned to their clock.
        //
        // Formula:  binanceTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        //                              + _timeOffsetMs
        //
        // _timeOffsetMs < 0 means our clock is ahead of Binance (most common
        // cause of the -1021 error). Adding a negative offset brings the
        // timestamp back to match what Binance expects.
        private long _timeOffsetMs = 0;
        private DateTime _lastTimeSync = DateTime.MinValue;
        private readonly SemaphoreSlim _timeSyncLock = new(1, 1);
        private static readonly TimeSpan TimeSyncInterval = TimeSpan.FromMinutes(10);

        // ── GetOpenAlgoOrders 20-second cache (prevents Binance 429) ──────
        // Binance rate-limits this endpoint aggressively; calling it on
        // every PositionSupervisor tick (every ~5s per symbol) was the
        // direct cause of the 429 bursts seen in production. A 20s TTL
        // is short enough to catch newly-placed conditional orders within
        // a reasonable window, but long enough to stay well inside the
        // per-minute request budget even with 20 tracked symbols.
        private readonly SemaphoreSlim _algoOrdersCacheLock = new(1, 1);
        private List<BinanceAlgoOrderInfo>? _algoOrdersCache;
        private string? _algoOrdersCacheSymbol;
        private DateTime _algoOrdersCacheExpiry = DateTime.MinValue;
        private static readonly TimeSpan AlgoOrdersCacheTtl = TimeSpan.FromSeconds(20);

        public BinanceAlgoOrderService(
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            TradingCredentialStore creds,
            ILogger<BinanceAlgoOrderService> logger)
        {
            _logger = logger;
            _creds = creds;

            // Fallback = single-tenant engine config (used only when no user LIVE session)
            _fallbackApiKey    = cfg["Binance:ApiKey"] ?? string.Empty;
            _fallbackApiSecret = cfg["Binance:SecretKey"] ?? cfg["Binance:ApiSecret"] ?? string.Empty;
            _baseUrl = (cfg["Binance:FuturesBaseUrl"] ?? "https://fapi.binance.com").TrimEnd('/');

            _http = httpFactory.CreateClient("BinanceAlgoRaw");
            _http.Timeout = TimeSpan.FromSeconds(8);
        }

        /// <summary>Per-user LIVE keys first, then appsettings fallback.</summary>
        private bool TryResolveKeys(out string apiKey, out string apiSecret)
        {
            // Trim — trailing newline/space in appsettings or UI paste → Binance -1022 INVALID_SIGNATURE
            if (_creds.TryGet(out _, out apiKey, out apiSecret))
            {
                apiKey = (apiKey ?? "").Trim();
                apiSecret = (apiSecret ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
                    return true;
            }
            apiKey = (_fallbackApiKey ?? "").Trim();
            apiSecret = (_fallbackApiSecret ?? "").Trim();
            return !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret);
        }

        // ── Timestamp helper ──────────────────────────────────────────────
        // Always call this instead of DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        // when building signed Binance requests. It applies the clock-offset
        // correction that prevents -1021 errors.
        private async Task<long> GetBinanceTimestampAsync(CancellationToken ct)
        {
            await EnsureTimeSyncedAsync(ct);
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _timeOffsetMs;
        }

        // Syncs clock offset with Binance server time.
        // Uses a semaphore so parallel callers don't all fire the sync at once.
        private async Task EnsureTimeSyncedAsync(CancellationToken ct)
        {
            // Fast path — no lock needed if sync is still fresh
            if (DateTime.UtcNow - _lastTimeSync < TimeSyncInterval)
                return;

            await _timeSyncLock.WaitAsync(ct);
            try
            {
                // Re-check inside lock
                if (DateTime.UtcNow - _lastTimeSync < TimeSyncInterval)
                    return;

                await SyncServerTimeAsync(ct);
            }
            finally
            {
                _timeSyncLock.Release();
            }
        }

        private async Task SyncServerTimeAsync(CancellationToken ct)
        {
            try
            {
                var url = $"{_baseUrl}/fapi/v1/time";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return;

                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);

                if (!doc.RootElement.TryGetProperty("serverTime", out var stProp)) return;

                long serverTimeMs = stProp.GetInt64();
                // Use RTT midpoint: measure local time BEFORE and AFTER the
                // HTTP round-trip, average them to compensate for network latency.
                long localTimeMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _timeOffsetMs     = serverTimeMs - localTimeMs;
                _lastTimeSync     = DateTime.UtcNow;
                _logger.LogInformation(
                    "[ALGO-RAW] Time sync OK: serverTime={srv} localTime={loc} offset={off}ms",
                    serverTimeMs, localTimeMs, _timeOffsetMs);

                if (Math.Abs(_timeOffsetMs) > 500)
                    _logger.LogWarning(
                        "[ALGO-RAW] Clock offset with Binance: {offset}ms — timestamps will be adjusted automatically",
                        _timeOffsetMs);
                else
                    _logger.LogDebug("[ALGO-RAW] Time synced. Offset={offset}ms", _timeOffsetMs);
            }
            catch (Exception ex)
            {
                // Non-fatal — worst case we use the local clock (may get -1021 again,
                // but we'll retry on the next call cycle).
                _logger.LogWarning(ex, "[ALGO-RAW] Time sync failed — using local clock");
            }
        }

        public async Task<bool> PlaceConditionalAsync(
            string symbol,
            OrderSide side,
            PositionSide positionSide,
            string type,
            decimal quantity,
            decimal triggerPrice,
            string workingType,
            bool? reduceOnly,
            CancellationToken ct,
            string? clientAlgoId = null)
        {
            if (!TryResolveKeys(out var apiKey, out var apiSecret))
            {
                _logger.LogError("[ALGO-RAW] Missing API credentials (no LIVE user keys and no config fallback)");
                return false;
            }

            var ts = await GetBinanceTimestampAsync(ct);
            string D(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);

            var q = new List<KeyValuePair<string, string>>
            {
                new("algoType",    "CONDITIONAL"),
                new("symbol",      symbol),
                new("side",        side == OrderSide.Buy ? "BUY" : "SELL"),
                new("type",        type),
                new("timestamp",   ts.ToString(CultureInfo.InvariantCulture)),
                new("recvWindow",  "10000"),
                new("workingType", workingType),
                new("triggerPrice", D(triggerPrice)),
                new("positionSide", positionSide.ToString().ToUpperInvariant()),
                new("quantity",    D(quantity))
            };

            if (!string.IsNullOrWhiteSpace(clientAlgoId))
                q.Add(new("clientAlgoId", clientAlgoId.Length > 32 ? clientAlgoId[..32] : clientAlgoId));

            if (reduceOnly.HasValue && positionSide == PositionSide.Both)
                q.Add(new("reduceOnly", reduceOnly.Value ? "true" : "false"));

            var (query, rawQuery) = BuildQuery(q);
            var sig = Sign(rawQuery, apiSecret);

            var url = $"{_baseUrl}/fapi/v1/algoOrder?{query}&signature={sig}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", apiKey);

            try
            {
                using var resp = await _http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    // -1021 means clock drift — force immediate re-sync on next call
                    if (body.Contains("-1021"))
                    {
                        _lastTimeSync = DateTime.MinValue;
                        _logger.LogWarning("[ALGO-RAW] -1021 on PlaceConditional — clock drift detected, will re-sync on next call");
                    }
                    _logger.LogError("[ALGO-RAW] HTTP {code} body={body}", (int)resp.StatusCode, body);
                    return false;
                }

                _logger.LogInformation("[ALGO-RAW] OK {symbol} {type} posSide={ps} trig={tp} body={body}",
                    symbol, type, positionSide, triggerPrice, body);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ALGO-RAW] EX PlaceConditionalAsync {symbol}", symbol);
                return false;
            }
        }

        public async Task<List<BinanceAlgoOrderInfo>> GetOpenAlgoOrdersAsync(string? symbol, CancellationToken ct)
        {
            var result = new List<BinanceAlgoOrderInfo>();
            if (!TryResolveKeys(out var apiKey, out var apiSecret))
            {
                _logger.LogError("[ALGO-RAW] Missing API credentials (no LIVE user keys and no config fallback)");
                return result;
            }

            // Fast path: return cached result if still fresh and for the same symbol.
            var now = DateTime.UtcNow;
            if (_algoOrdersCache != null &&
                _algoOrdersCacheSymbol == symbol &&
                now < _algoOrdersCacheExpiry)
            {
                return _algoOrdersCache;
            }

            await _algoOrdersCacheLock.WaitAsync(ct);
            try
            {
                // Re-check inside the lock
                now = DateTime.UtcNow;
                if (_algoOrdersCache != null &&
                    _algoOrdersCacheSymbol == symbol &&
                    now < _algoOrdersCacheExpiry)
                {
                    return _algoOrdersCache;
                }

                var ts = await GetBinanceTimestampAsync(ct);
                var q = new List<KeyValuePair<string, string>>
                {
                    new("timestamp",  ts.ToString(CultureInfo.InvariantCulture)),
                    new("recvWindow", "10000"),
                };
                if (!string.IsNullOrEmpty(symbol)) q.Add(new("symbol", symbol));

                var (query, rawQuery) = BuildQuery(q);
                var sig = Sign(rawQuery, apiSecret);
                var url = $"{_baseUrl}/fapi/v1/openAlgoOrders?{query}&signature={sig}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", apiKey);

                try
                {
                    using var resp = await _http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        // -1021 clock drift — force re-sync, don't cache the error
                        if (body.Contains("-1021"))
                        {
                            _lastTimeSync = DateTime.MinValue;
                            _logger.LogWarning("[ALGO-RAW] -1021 on GetOpenAlgoOrders — clock drift detected, offset will be re-synced on next call. Current offset={off}ms", _timeOffsetMs);
                        }
                        else
                        {
                            if (body.Contains("-1022"))
                                _logger.LogError(
                                    "[ALGO-RAW] GetOpenAlgoOrders -1022 INVALID_SIGNATURE — check API Key/Secret pair (trim whitespace), Futures permission, HMAC key type. body={body}",
                                    body);
                            else
                                _logger.LogError("[ALGO-RAW] GetOpenAlgoOrders HTTP {code} body={body}", (int)resp.StatusCode, body);
                        }
                        return result;
                    }

                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

                    foreach (var o in doc.RootElement.EnumerateArray())
                    {
                        decimal GetDec(string name) =>
                            o.TryGetProperty(name, out var v) && decimal.TryParse(v.GetString(), CultureInfo.InvariantCulture, out var d) ? d : 0m;
                        string GetStr(string name) => o.TryGetProperty(name, out var v) ? (v.GetString() ?? "") : "";
                        long GetLong(string name) =>
                            o.TryGetProperty(name, out var v)
                                ? (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l
                                   : long.TryParse(v.GetString(), out var l2) ? l2 : 0L)
                                : 0L;
                        string? GetClientId(string name) => o.TryGetProperty(name, out var v) ? v.GetString() : null;

                        result.Add(new BinanceAlgoOrderInfo
                        {
                            AlgoId = GetLong("algoId"),
                            ClientAlgoId = GetClientId("clientAlgoId"),
                            Symbol = GetStr("symbol"),
                            Side = GetStr("side").Equals("BUY", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell,
                            PositionSide = Enum.TryParse<PositionSide>(GetStr("positionSide"), true, out var ps) ? ps : PositionSide.Both,
                            OrderType = GetStr("orderType"),
                            TriggerPrice = GetDec("triggerPrice"),
                            Quantity = GetDec("quantity"),
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ALGO-RAW] EX GetOpenAlgoOrdersAsync {symbol}", symbol);
                }

                // Cache even an empty result to avoid hammering the endpoint
                _algoOrdersCache = result;
                _algoOrdersCacheSymbol = symbol;
                _algoOrdersCacheExpiry = DateTime.UtcNow.Add(AlgoOrdersCacheTtl);
                return result;
            }
            finally
            {
                _algoOrdersCacheLock.Release();
            }
        }

        public async Task<bool> CancelAlgoOrderAsync(long algoId, CancellationToken ct)
        {
            if (!TryResolveKeys(out var apiKey, out var apiSecret)) return false;

            var ts = await GetBinanceTimestampAsync(ct);
            var q = new List<KeyValuePair<string, string>>
            {
                new("algoId",     algoId.ToString(CultureInfo.InvariantCulture)),
                new("timestamp",  ts.ToString(CultureInfo.InvariantCulture)),
                new("recvWindow", "10000"),
            };
            var (query, rawQuery) = BuildQuery(q);
            var sig = Sign(rawQuery, apiSecret);
            var url = $"{_baseUrl}/fapi/v1/algoOrder?{query}&signature={sig}";

            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", apiKey);

            try
            {
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    if (body.Contains("-1021")) _lastTimeSync = DateTime.MinValue;
                    _logger.LogWarning("[ALGO-RAW] CancelAlgoOrder HTTP {code} body={body}", (int)resp.StatusCode, body);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ALGO-RAW] EX CancelAlgoOrderAsync algoId={id}", algoId);
                return false;
            }
        }

        
        /// <summary>
        /// Binance HMAC totalParams = query string EXACTLY as sent (minus signature).
        /// Do NOT alphabetically re-order — official examples use insertion order.
        /// Sign the same string appended to the URL.
        /// </summary>
        private static (string queryForUrl, string totalParams) BuildQuery(IEnumerable<KeyValuePair<string, string>> q)
        {
            var parts = new List<string>();
            foreach (var kv in q)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                // Values for openAlgoOrders are alnum — no encoding difference
                parts.Add(kv.Key + "=" + kv.Value);
            }
            var totalParams = string.Join("&", parts);
            // URL uses identical totalParams (no EscapeDataString on numbers/symbols)
            return (totalParams, totalParams);
        }

        private static string Sign(string totalParams, string secret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(totalParams));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}

