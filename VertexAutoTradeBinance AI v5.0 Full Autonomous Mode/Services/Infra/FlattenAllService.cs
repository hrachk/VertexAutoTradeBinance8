using System.Text.Json;
using Binance.Net.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.Infra;

/// <summary>
/// Emergency flatten: cancel open + algo orders, market-close all Live positions,
/// force-close Demo ledgers under SharedData, report via Telegram.
/// </summary>
public sealed class FlattenAllService
{
    private readonly BinanceClientFactory _factory;
    private readonly BinanceAlgoOrderService _algo;
    private readonly IConfiguration _cfg;
    private readonly ILogger<FlattenAllService> _log;
    private readonly Polly.Retry.AsyncRetryPolicy _retry;

    public FlattenAllService(
        BinanceClientFactory factory,
        BinanceAlgoOrderService algo,
        IConfiguration cfg,
        ILogger<FlattenAllService> log)
    {
        _factory = factory;
        _algo = algo;
        _cfg = cfg;
        _log = log;
        _retry = BinanceResilience.CreateRetryPolicy(log);
    }

    public async Task<string> ExecuteAsync(string reason, CancellationToken ct = default)
    {
        var lines = new List<string> { $"🛑 FLATTEN ALL — {reason}", $"UTC {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}" };
        decimal totalPnl = 0m;

        // ---- LIVE ----
        try
        {
            var client = _factory.TryCreateRestClient();
            if (client == null)
            {
                lines.Add("LIVE: no API credentials — skip exchange flatten");
            }
            else
            {
                var posRes = await _retry.ExecuteAsync(() =>
                    client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct));
                if (!posRes.Success || posRes.Data == null)
                {
                    lines.Add($"LIVE: get positions failed: {posRes.Error?.Message}");
                }
                else
                {
                    var open = posRes.Data.Where(p => p.Quantity != 0).ToList();
                    lines.Add($"LIVE open positions: {open.Count}");
                    foreach (var p in open)
                    {
                        var symbol = p.Symbol;
                        try
                        {
                            // Cancel regular open orders
                            var openOrders = await _retry.ExecuteAsync(() =>
                                client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct));
                            if (openOrders.Success && openOrders.Data != null)
                            {
                                foreach (var o in openOrders.Data)
                                {
                                    try
                                    {
                                        await _retry.ExecuteAsync(() =>
                                            client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, o.Id, ct: ct));
                                    }
                                    catch (Exception ex) { _log.LogDebug(ex, "cancel order"); }
                                }
                            }

                            // Cancel algo / conditional
                            try
                            {
                                var algos = await _algo.GetOpenAlgoOrdersAsync(symbol, ct);
                                if (algos != null)
                                {
                                    foreach (var a in algos)
                                    {
                                        try { await _algo.CancelAlgoOrderAsync(a.AlgoId, ct); } catch { }
                                    }
                                }
                            }
                            catch (Exception ex) { _log.LogDebug(ex, "algo cancel {s}", symbol); }

                            var qty = Math.Abs(p.Quantity);
                            var side = p.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy;
                            var close = await _retry.ExecuteAsync(() =>
                                client.UsdFuturesApi.Trading.PlaceOrderAsync(
                                    symbol: symbol,
                                    side: side,
                                    type: FuturesOrderType.Market,
                                    quantity: qty,
                                    positionSide: p.PositionSide,
                                    reduceOnly: null,
                                    ct: ct));
                            if (close.Success)
                            {
                                decimal upnl = p.UnrealizedProfit;
                                totalPnl += upnl;
                                lines.Add($"LIVE CLOSE {symbol} {p.PositionSide} qty={qty} uPnL≈{upnl:F2}");
                            }
                            else
                                lines.Add($"LIVE FAIL {symbol}: {close.Error?.Message}");
                        }
                        catch (Exception ex)
                        {
                            lines.Add($"LIVE ERR {symbol}: {ex.Message}");
                            _log.LogError(ex, "[FLATTEN] {sym}", symbol);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add($"LIVE exception: {ex.Message}");
            _log.LogError(ex, "[FLATTEN] live");
        }

        // ---- DEMO (all client_* folders) ----
        try
        {
            var root = _cfg["SharedData:Root"] ?? @"C:\Vertex\Engines";
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.GetDirectories(root, "client_*"))
                {
                    var path = Path.Combine(dir, "demo-account.json");
                    if (!File.Exists(path)) continue;
                    var closed = FlattenDemoFile(path, out var demoPnl);
                    totalPnl += demoPnl;
                    lines.Add($"DEMO {Path.GetFileName(dir)}: closed {closed} pos, PnL≈{demoPnl:F2}");
                }
            }
            // Wake Web DemoAccountService
            try
            {
                var flag = Path.Combine(_cfg["SharedData:Root"] ?? root, "flatten_demo.flag");
                File.WriteAllText(flag, DateTime.UtcNow.ToString("o") + "\n" + reason);
            }
            catch { }
        }
        catch (Exception ex)
        {
            lines.Add($"DEMO exception: {ex.Message}");
        }

        lines.Add($"TOTAL est. PnL impact ≈ {totalPnl:F2}");
        var report = string.Join("\n", lines);
        _log.LogWarning("[FLATTEN]\n{report}", report);
        return report;
    }

    private int FlattenDemoFile(string path, out decimal pnl)
    {
        pnl = 0m;
        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Positions", out var posArr) || posArr.ValueKind != JsonValueKind.Array)
                return 0;

            // Prefer structured rewrite via mutable dictionary
            var state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (state == null) return 0;

            var positions = new List<JsonElement>();
            if (state.TryGetValue("Positions", out var pEl) && pEl.ValueKind == JsonValueKind.Array)
                positions = pEl.EnumerateArray().ToList();

            if (positions.Count == 0) return 0;

            // Serialize minimal close: empty positions, history append is best-effort
            var opts = new JsonSerializerOptions { WriteIndented = true };
            // Use dynamic approach: load as JsonNode
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node == null) return 0;
            var posNode = node["Positions"] as System.Text.Json.Nodes.JsonArray;
            if (posNode == null) return 0;
            int n = posNode.Count;
            // Estimate pnl from UnrealizedPnl if present
            foreach (var item in posNode)
            {
                if (item?["UnrealizedPnl"] != null && decimal.TryParse(item["UnrealizedPnl"]!.ToString(), out var u))
                    pnl += u;
            }
            posNode.Clear();
            // cancel pending
            if (node["PendingOrders"] is System.Text.Json.Nodes.JsonArray pend)
                pend.Clear();
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return n;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[FLATTEN] demo file {p}", path);
            return 0;
        }
    }
}
