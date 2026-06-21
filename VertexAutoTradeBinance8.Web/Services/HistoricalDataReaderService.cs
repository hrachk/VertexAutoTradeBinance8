using System.Text.Json;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services
{
    /// <summary>
    /// Read-only access to the file-based historical data archive
    /// (datadb/SYMBOL/TF.json) that the Engine's HistoricalDataLoaderService
    /// independently maintains. This is a SEPARATE source from
    /// MarketSnapshotFileService's klines_bootstrap.json (the live trading
    /// buffer's periodic snapshot, capped at a few hundred bars) — datadb/
    /// is the deep, permanent archive this project is building specifically
    /// so charts aren't limited to what the trading strategy happens to
    /// need in memory right now.
    ///
    /// Deliberately does NOT write anything — only the Engine-side loader
    /// owns writes to this folder, to avoid any risk of the Web process
    /// corrupting a file mid-write from the loader's own perspective.
    /// </summary>
    public sealed class HistoricalDataReaderService
    {
        private readonly string _root;

        public HistoricalDataReaderService(IConfiguration cfg)
        {
            // Same SharedData:Root convention as every other cross-process
            // file in this project — the Engine writes here, this just
            // needs to agree on where "here" is.
            var root = cfg["SharedData:Root"];
            _root = !string.IsNullOrWhiteSpace(root)
                ? Path.Combine(root, "datadb")
                : Path.Combine(AppContext.BaseDirectory, "datadb");
        }

        private static string SafeSegment(string s) =>
            string.Concat(s.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));

        private string FilePath(string symbol, string timeframeLabel) =>
            Path.Combine(_root, SafeSegment(symbol.ToUpperInvariant()), $"{SafeSegment(timeframeLabel)}.json");

        public bool Has(string symbol, string timeframeLabel) =>
            File.Exists(FilePath(symbol, timeframeLabel));

        /// <summary>
        /// Loads all archived bars for a symbol+timeframe label (e.g.
        /// "15m", "1h" — same labels the Engine's loader uses). Returns
        /// an empty list if nothing is archived, never throws for a
        /// missing file.
        /// </summary>
        public async Task<List<KlineDto>> LoadAsync(string symbol, string timeframeLabel, CancellationToken ct = default)
        {
            var path = FilePath(symbol, timeframeLabel);
            if (!File.Exists(path)) return new List<KlineDto>();

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var data = await JsonSerializer.DeserializeAsync<List<KlineDto>>(fs, cancellationToken: ct);
                return data ?? new List<KlineDto>();
            }
            catch
            {
                // A read landing mid-write by the Engine's own atomic
                // rename, or a genuinely corrupt file — either way, fail
                // soft and return nothing rather than crashing the chart.
                return new List<KlineDto>();
            }
        }

        /// <summary>
        /// Loads bars strictly before a given OpenTime (milliseconds),
        /// oldest-first, capped at `count` — the exact shape a "scroll the
        /// chart further back" request needs: give me older history than
        /// what's already on screen.
        /// </summary>
        public async Task<List<KlineDto>> LoadBeforeAsync(string symbol, string timeframeLabel, long beforeOpenTimeMs, int count, CancellationToken ct = default)
        {
            var all = await LoadAsync(symbol, timeframeLabel, ct);
            var older = all.Where(k => k.OpenTime < beforeOpenTimeMs).ToList();
            return older.Count <= count ? older : older.GetRange(older.Count - count, count);
        }

        /// <summary>
        /// Every symbol currently archived on disk, regardless of whether
        /// it's part of the live trading universe — lets the UI show what
        /// history actually exists vs what's just live-traded right now.
        /// </summary>
        public IReadOnlyList<string> ListArchivedSymbols()
        {
            if (!Directory.Exists(_root)) return Array.Empty<string>();
            return Directory.GetDirectories(_root)
                .Select(Path.GetFileName)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .OrderBy(s => s)
                .ToList();
        }

        /// <summary>
        /// Every timeframe label archived for a given symbol (e.g.
        /// ["15m", "1h", "5m"]) — derived from whatever .json files exist
        /// in that symbol's folder, not a hardcoded list.
        /// </summary>
        public IReadOnlyList<string> ListArchivedTimeframes(string symbol)
        {
            var dir = Path.Combine(_root, SafeSegment(symbol.ToUpperInvariant()));
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .OrderBy(s => s)
                .ToList();
        }
    }
}
