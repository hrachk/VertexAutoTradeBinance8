using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Binance.Net.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services
{
    public interface IAlgoOrderRawClient
    {
        Task<bool> PlaceConditionalAsync(
            string symbol,
            OrderSide side,
            PositionSide positionSide,
            string type,
            decimal? quantity,
            decimal triggerPrice,
            string workingType,
            bool? reduceOnly,
            bool closePosition,
            CancellationToken ct);
    }

    /// <summary>
    /// RAW POST /fapi/v1/algoOrder.
    /// Вынесен из приватного вложенного класса PositionSupervisorService,
    /// чтобы OrderExecutor и Supervisor использовали ОДИН путь фолбэка (-4120).
    ///
    /// Изменения против оригинала:
    ///   - quantity стал nullable + добавлен closePosition (для стопа на весь остаток).
    ///   - reduceOnly по-прежнему не отправляется в Hedge Mode (иначе -1106).
    /// </summary>
    public sealed class AlgoOrderRawClient : IAlgoOrderRawClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<AlgoOrderRawClient> _logger;
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly string _baseUrl;

        public AlgoOrderRawClient(
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            ILogger<AlgoOrderRawClient> logger)
        {
            _logger = logger;

            _apiKey = cfg["Binance:ApiKey"] ?? string.Empty;
            _apiSecret = cfg["Binance:ApiSecret"] ?? string.Empty;
            _baseUrl = (cfg["Binance:FuturesBaseUrl"] ?? "https://fapi.binance.com").TrimEnd('/');

            _http = httpFactory.CreateClient("BinanceAlgoRaw");
            _http.Timeout = TimeSpan.FromSeconds(8);
        }

        public async Task<bool> PlaceConditionalAsync(
            string symbol,
            OrderSide side,
            PositionSide positionSide,
            string type,
            decimal? quantity,
            decimal triggerPrice,
            string workingType,
            bool? reduceOnly,
            bool closePosition,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
            {
                _logger.LogError("[ALGO-RAW] Missing Binance:ApiKey / Binance:ApiSecret in config");
                return false;
            }

            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string D(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);

            var q = new List<KeyValuePair<string, string>>
            {
                new("algoType",     "CONDITIONAL"),
                new("symbol",       symbol),
                new("side",         side == OrderSide.Buy ? "BUY" : "SELL"),
                new("type",         type),
                new("timestamp",    ts.ToString(CultureInfo.InvariantCulture)),
                new("workingType",  workingType),
                new("triggerPrice", D(triggerPrice)),
                new("positionSide", positionSide.ToString().ToUpperInvariant())
            };

            if (closePosition)
                q.Add(new("closePosition", "true"));
            else if (quantity.HasValue && quantity.Value > 0)
                q.Add(new("quantity", D(quantity.Value)));
            else
                return false;

            // reduceOnly нельзя слать ни в Hedge, ни вместе с closePosition
            if (reduceOnly.HasValue && !closePosition && positionSide == PositionSide.Both)
                q.Add(new("reduceOnly", reduceOnly.Value ? "true" : "false"));

            var query = BuildQuery(q);
            var sig = Sign(query, _apiSecret);
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

                _logger.LogInformation("[ALGO-RAW] OK {symbol} {type} posSide={ps} trig={tp}",
                    symbol, type, positionSide, triggerPrice);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ALGO-RAW] EX PlaceConditionalAsync {symbol}", symbol);
                return false;
            }
        }

        private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> q)
        {
            var sb = new StringBuilder();
            foreach (var kv in q)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(kv.Value));
            }
            return sb.ToString();
        }

        private static string Sign(string queryString, string secret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
