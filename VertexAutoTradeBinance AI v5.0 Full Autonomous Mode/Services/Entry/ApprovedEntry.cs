namespace VertexAutoTradeBinance8.Services.Entry;

/// <summary>
/// Engine-approved entry after full PROC filters + RiskManager sizing + 1m confirm.
/// Demo must open ONLY from these records (parity 1:1 with Live decision path).
/// </summary>
public sealed class ApprovedEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = ""; // Buy/Sell or LONG/SHORT
    public decimal Entry { get; set; }
    public decimal StopLoss { get; set; }
    public List<decimal> TakeProfits { get; set; } = new();
    public decimal Qty { get; set; }
    public decimal Leverage { get; set; }
    public decimal Confidence { get; set; }
    public string Reason { get; set; } = "";
    public string Strategy { get; set; } = "";
    public decimal SizeMultiplier { get; set; } = 1m;
    public decimal InitialRiskPrice { get; set; }
}

public interface IApprovedEntryPublisher
{
    void Publish(ApprovedEntry entry);
}

public interface IApprovedEntryReader
{
    Task<IReadOnlyList<ApprovedEntry>> LoadRecentAsync(TimeSpan maxAge, CancellationToken ct = default);
}

/// <summary>File bus under SharedData:Root/approved_entries.json (newest first, capped).</summary>
public sealed class ApprovedEntryFileBus : IApprovedEntryPublisher, IApprovedEntryReader
{
    private readonly string _path;
    private readonly ILogger<ApprovedEntryFileBus> _log;
    private readonly object _lock = new();
    private const int MaxKeep = 200;

    public ApprovedEntryFileBus(IConfiguration cfg, ILogger<ApprovedEntryFileBus> log)
    {
        var root = cfg["SharedData:Root"] ?? @"C:\Vertex\Engines\client_001";
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "approved_entries.json");
        _log = log;
    }

    public void Publish(ApprovedEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Symbol) || entry.Qty <= 0) return;
        try
        {
            lock (_lock)
            {
                var list = LoadUnsafe();
                list.Insert(0, entry);
                if (list.Count > MaxKeep) list = list.Take(MaxKeep).ToList();
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
                File.Move(tmp, _path, overwrite: true);
            }
            _log.LogInformation(
                "[ENTRY-PIPE] APPROVED {sym} {side} qty={q} lev={lev} entry={e} sl={sl}",
                entry.Symbol, entry.Side, entry.Qty, entry.Leverage, entry.Entry, entry.StopLoss);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ENTRY-PIPE] publish failed {sym}", entry.Symbol);
        }
    }

    public Task<IReadOnlyList<ApprovedEntry>> LoadRecentAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        try
        {
            lock (_lock)
            {
                var cutoff = DateTime.UtcNow - maxAge;
                var list = LoadUnsafe().Where(x => x.Utc >= cutoff).ToList();
                return Task.FromResult((IReadOnlyList<ApprovedEntry>)list);
            }
        }
        catch
        {
            return Task.FromResult((IReadOnlyList<ApprovedEntry>)Array.Empty<ApprovedEntry>());
        }
    }

    private List<ApprovedEntry> LoadUnsafe()
    {
        if (!File.Exists(_path)) return new List<ApprovedEntry>();
        try
        {
            var json = File.ReadAllText(_path);
            return System.Text.Json.JsonSerializer.Deserialize<List<ApprovedEntry>>(json) ?? new();
        }
        catch { return new List<ApprovedEntry>(); }
    }
}
