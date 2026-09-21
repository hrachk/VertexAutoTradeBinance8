using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.News;

/// <summary>
/// Lightweight liquidity / slippage risk tags during news windows (Category C + any active directive).
/// Does not call Binance REST — uses optional depth score pushed by SmartFlow/LiquidityCluster.
/// </summary>
public sealed class NewsMicrostructureGate
{
    private readonly ILogger<NewsMicrostructureGate> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, (decimal depthScore, decimal spreadMult, DateTime utc)> _snap = new(StringComparer.OrdinalIgnoreCase);

    public NewsMicrostructureGate(ILogger<NewsMicrostructureGate> log) => _log = log;

    /// <param name="depthScore">0..1 relative book depth near mid (1 = healthy).</param>
    /// <param name="spreadVsMedian">spread / median30m (1 = normal, 2 = 2x wide).</param>
    public void Update(string symbol, decimal depthScore, decimal spreadVsMedian)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        lock (_gate)
        {
            _snap[symbol] = (Math.Clamp(depthScore, 0m, 1m), Math.Max(0.1m, spreadVsMedian), DateTime.UtcNow);
            if (_snap.Count > 400)
            {
                foreach (var k in _snap.Where(kv => (DateTime.UtcNow - kv.Value.utc).TotalMinutes > 30).Select(kv => kv.Key).Take(50).ToList())
                    _snap.Remove(k);
            }
        }
    }

    public bool TryGetRisk(string symbol, out string reasonCode, out string detail)
    {
        reasonCode = "";
        detail = "";
        lock (_gate)
        {
            if (!_snap.TryGetValue(symbol, out var s)) return false;
            if ((DateTime.UtcNow - s.utc).TotalSeconds > 90) return false;

            if (s.spreadMult >= 1.2m)
            {
                reasonCode = NewsReasonCodes.SpreadExpansion;
                detail = $"spread×{s.spreadMult:F2} ≥ 1.2 median";
                return true;
            }
            if (s.depthScore < 0.35m)
            {
                reasonCode = NewsReasonCodes.LowDepth;
                detail = $"depthScore={s.depthScore:F2} < 0.35";
                return true;
            }
            if (s.spreadMult >= 2.5m)
            {
                reasonCode = NewsReasonCodes.HighSlippage;
                detail = $"spread×{s.spreadMult:F2} ≥ 2.5 (slippage proxy)";
                return true;
            }
        }
        return false;
    }
}
