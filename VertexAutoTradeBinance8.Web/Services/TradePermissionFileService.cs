using System.Text.Json;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class TradePermissionFileService
{
    private readonly string _traceDir;

    public TradePermissionFileService(IConfiguration cfg)
    {
        

        _traceDir = Path.Combine(AppContext.BaseDirectory, "ai-models", "decision-trace");
    }

    public async Task<IReadOnlyList<TradePermissionEvent>> LoadLatestAsync()
    {
        var file = Directory
            .GetFiles(_traceDir, "decision_trace_*.jsonl")
            .OrderByDescending(f => f)
            .FirstOrDefault();

        if (file == null)
            return [];

        var all = new List<TradePermissionEvent>();

        using var fs = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        using var sr = new StreamReader(fs);

        while (!sr.EndOfStream)
        {
            var line = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var e = JsonSerializer.Deserialize<TradePermissionEvent>(line);
                if (e != null)
                    all.Add(e);
            }
            catch
            {
                // пропускаем битую строку
            }
        }

        // ⚠️ ВАЖНО: берём ПОСЛЕДНЕЕ решение
        return all
            .GroupBy(x => (x.Symbol, x.Timeframe))
            .Select(g => g.OrderByDescending(x => x.Time).First())
            .OrderByDescending(x => x.Time)
            .ToList();
    }
}
