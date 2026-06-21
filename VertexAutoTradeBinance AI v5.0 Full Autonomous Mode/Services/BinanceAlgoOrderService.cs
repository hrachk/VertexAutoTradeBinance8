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
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly string _baseUrl;

        public BinanceAlgoOrderService(IConfiguration cfg, IHttpClientFactory httpFactory, ILogger<BinanceAlgoOrderService> logger)
        {
            _logger = logger;

            _apiKey = cfg["Binance:ApiKey"] ?? string.Empty;
            _apiSecret = cfg["Binance:SecretKey"] ?? cfg["Binance:ApiSecret"] ?? string.Empty;
            _baseUrl = (cfg["Binance:FuturesBaseUrl"] ?? "https://fapi.binance.com").TrimEnd('/');

            _http = httpFactory.CreateClient("BinanceAlgoRaw");
            _http.Timeout = TimeSpan.FromSeconds(8);
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
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
            {
                _logger.LogError("[ALGO-RAW] Missing Binance:ApiKey / Binance:SecretKey in config");
                return false;
            }

            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string D(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);

            var q = new List<KeyValuePair<string, string>>
            {
                new("algoType", "CONDITIONAL"),
                new("symbol", symbol),
                new("side", side == OrderSide.Buy ? "BUY" : "SELL"),
                new("type", type),
                new("timestamp", ts.ToString(CultureInfo.InvariantCulture)),
                new("workingType", workingType),
                new("triggerPrice", D(triggerPrice)),
                new("positionSide", positionSide.ToString().ToUpperInvariant()),
                new("quantity", D(quantity))
            };

            if (reduceOnly.HasValue && positionSide == PositionSide.Both)
                q.Add(new("reduceOnly", reduceOnly.Value ? "true" : "false"));

            var (query, rawQuery) = BuildQuery(q);
            var sig = Sign(rawQuery, _apiSecret);

            var url = $"{_baseUrl}/fapi/v1/algoOrder?{query}&signature={sig}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);

            try
            {
                using var resp = await _http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
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
            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
            {
                _logger.LogError("[ALGO-RAW] Missing Binance:ApiKey / Binance:SecretKey in config");
                return result;
            }

            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var q = new List<KeyValuePair<string, string>>
            {
                new("timestamp", ts.ToString(CultureInfo.InvariantCulture)),
            };
            if (!string.IsNullOrEmpty(symbol)) q.Add(new("symbol", symbol));

            var (query, rawQuery) = BuildQuery(q);
            var sig = Sign(rawQuery, _apiSecret);
            var url = $"{_baseUrl}/fapi/v1/openAlgoOrders?{query}&signature={sig}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);

            try
            {
                using var resp = await _http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogError("[ALGO-RAW] GetOpenAlgoOrders HTTP {code} body={body}", (int)resp.StatusCode, body);
                    return result;
                }

                // CONFIRMED real response shape via official Binance docs:
                // a plain top-level JSON array, not wrapped in an "orders" property.
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
            return result;
        }

        public async Task<bool> CancelAlgoOrderAsync(long algoId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret)) return false;

            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var q = new List<KeyValuePair<string, string>>
            {
                new("algoId", algoId.ToString(CultureInfo.InvariantCulture)),
                new("timestamp", ts.ToString(CultureInfo.InvariantCulture)),
            };
            var (query, rawQuery) = BuildQuery(q);
            var sig = Sign(rawQuery, _apiSecret);
            var url = $"{_baseUrl}/fapi/v1/algoOrder?{query}&signature={sig}";

            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);

            try
            {
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
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

        private static (string encoded, string raw) BuildQuery(IEnumerable<KeyValuePair<string, string>> q)
        {
            var encoded = new StringBuilder();
            var raw = new StringBuilder();

            foreach (var kv in q)
            {
                if (encoded.Length > 0) { encoded.Append('&'); raw.Append('&'); }
                raw.Append(kv.Key).Append('=').Append(kv.Value);
                encoded.Append(Uri.EscapeDataString(kv.Key))
                       .Append('=')
                       .Append(Uri.EscapeDataString(kv.Value));
            }

            return (encoded.ToString(), raw.ToString());
        }

        private static string Sign(string rawQueryString, string secret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawQueryString));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
