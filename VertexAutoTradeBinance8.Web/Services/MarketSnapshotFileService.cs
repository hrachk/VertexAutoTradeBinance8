using System.Text.Json;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class MarketSnapshotFileService
{
    private readonly string _file;

    public MarketSnapshotFileService(IConfiguration cfg)
    {
        var root = cfg["SharedData:Root"]
            ?? throw new InvalidOperationException("SharedData:Root missing");

        _file = Path.Combine(AppContext.BaseDirectory, "market", "klines_bootstrap.json");

    }

    public async Task<IReadOnlyList<MarketSeries>> LoadAsync()
    {
        if (!File.Exists(_file))
            return [];

        using var fs = new FileStream(
            _file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        using var doc = await JsonDocument.ParseAsync(fs);

        var list = new List<MarketSeries>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var parts = prop.Name.Split(':');
            if (parts.Length != 2) continue;

            var klines = new List<KlineDto>();

            foreach (var k in prop.Value.EnumerateArray())
            {
                klines.Add(new KlineDto(
                    k[0].GetInt64(),
                    k[1].GetDecimal(),
                    k[2].GetDecimal(),
                    k[3].GetDecimal(),
                    k[4].GetDecimal(),
                    k[5].GetDecimal()
                ));
            }

            list.Add(new MarketSeries(parts[0], parts[1], klines));
        }

        return list;
    }
}

   