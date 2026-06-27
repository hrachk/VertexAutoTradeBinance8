using System.Text.Json;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Reads the real Engine's DCA purchase history (dca_state.json) for
/// display on the Settings page. The Engine's own DcaService runs as
/// a BackgroundService in a completely separate OS process — this is
/// a simple read-only file reader, not a live object reference,
/// matching the same cross-process file-sharing approach already used
/// throughout this project.
/// </summary>
public sealed class RealDcaHistoryReader
{
    private readonly string _path;

    public RealDcaHistoryReader(IConfiguration cfg)
    {
        var root = cfg["SharedData:Root"] ?? AppContext.BaseDirectory;
        _path = Path.Combine(root, "dca_state.json");
    }

    public DcaState Read()
    {
        try
        {
            if (!File.Exists(_path)) return new DcaState();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<DcaState>(json) ?? new DcaState();
        }
        catch
        {
            return new DcaState();
        }
    }
}
