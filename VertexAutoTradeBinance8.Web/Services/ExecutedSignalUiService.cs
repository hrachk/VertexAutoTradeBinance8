using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

public class ExecutedSignalUiService
{
    private readonly ExecutedSignalService _core;
    private readonly HttpClient _http;

    private readonly Dictionary<string, decimal> _prices = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, LivePosition> _positions = new();


    public ExecutedSignalUiService(
        ExecutedSignalService core,
        IHttpClientFactory factory)
    {
        _core = core;
        _http = factory.CreateClient("BinancePublic");
    }

    public List<ExecutedSignalRecord> GetAll() => _core.GetAll();

    public decimal GetPrice(string symbol)
    {
        lock (_lock)
            return _prices.TryGetValue(symbol, out var p) ? p : 0m;
    }

    public async Task UpdatePricesAsync(IEnumerable<string> symbols)
    {
        foreach (var s in symbols.Distinct())
        {
            try
            {
                var r = await _http.GetFromJsonAsync<BinancePrice>(
                    $"https://fapi.binance.com/fapi/v1/ticker/price?symbol={s}");

                if (r != null)
                {
                    lock (_lock)
                        _prices[s] = r.Price;
                }
            }
            catch { }
        }
    }

    public async Task UpdatePositionsAsync()
    {
        try
        {
            var list = await _http.GetFromJsonAsync<List<LivePosition>>(
                "/fapi/v2/positionRisk");

            if (list == null) return;

            lock (_lock)
            {
                _positions.Clear();

                foreach (var p in list.Where(p => Math.Abs(p.PositionAmt) > 0))
                {
                    var key = $"{p.Symbol}:{(p.PositionAmt > 0 ? "LONG" : "SHORT")}";
                    _positions[key] = p;
                }
            }
        }
        catch
        {
            // молча, UI не должен падать
        }
    }

    public LivePosition? GetLivePosition(string symbol, SignalSide side)
    {
        var key = $"{symbol}:{(side == SignalSide.Buy ? "LONG" : "SHORT")}";
        lock (_lock)
            return _positions.TryGetValue(key, out var p) ? p : null;
    }


    private sealed class BinancePrice
    {
        public decimal Price { get; set; }
    }

    public sealed class LivePosition
    {
        public string Symbol { get; set; } = "";
        public decimal PositionAmt { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal MarkPrice { get; set; }
        public decimal UnrealizedProfit { get; set; }
        public decimal IsolatedMargin { get; set; }
        public decimal Leverage { get; set; }
        public decimal LiquidationPrice { get; set; }
        public string PositionSide { get; set; } = "BOTH";
    }
}
