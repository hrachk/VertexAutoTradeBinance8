using System.Text.Json;
using System.Text.Json.Serialization;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// The only writer of missed_trades.json (the /missed-trades signal log).
    ///
    /// Two services used to write this file with different JSON conventions:
    /// SimulatedTradeService wrote camelCase and LiveSignalService wrote
    /// PascalCase, each reading back case-sensitively. Whichever wrote next
    /// deserialised the other's records as empty objects (Symbol "", Time
    /// 0001-01-01) and saved them back, so history turned into blank rows.
    /// One appended at the end and trimmed the front while the other inserted
    /// at the front, so trims also deleted the newest records.
    ///
    /// Now: one process-wide lock, case-insensitive reads, camelCase writes,
    /// blank records dropped, oldest → newest order, newest N kept.
    /// </summary>
    public static class MissedTradeStore
    {
        public const int MaxRecords = 500;

        private static readonly SemaphoreSlim Gate = new(1, 1);

        private static readonly JsonSerializerOptions ReadOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            AllowTrailingCommas = true,
        };

        private static readonly JsonSerializerOptions WriteOpts = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>A record with no symbol or no timestamp carries no information.</summary>
        public static bool IsBlank(MissedTradeRecord? r)
            => r is null || string.IsNullOrWhiteSpace(r.Symbol) || r.Time.Year < 2020;

        public static async Task AppendAsync(string path, MissedTradeRecord record, CancellationToken ct = default)
        {
            if (IsBlank(record)) return;

            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var list = await ReadAsync(path, ct).ConfigureAwait(false);
                list.Add(record);

                list = list
                    .Where(r => !IsBlank(r))
                    .OrderBy(r => r.Time)
                    .ToList();
                if (list.Count > MaxRecords)
                    list.RemoveRange(0, list.Count - MaxRecords);

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var tmp = path + ".tmp";
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(list, WriteOpts), ct)
                    .ConfigureAwait(false);

                // The Web reads this file every 5 s; retry briefly if it is open.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Move(tmp, path, overwrite: true);
                        break;
                    }
                    catch (IOException) when (attempt < 8)
                    {
                        await Task.Delay(40 * (attempt + 1), ct).ConfigureAwait(false);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 8)
                    {
                        await Task.Delay(40 * (attempt + 1), ct).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<List<MissedTradeRecord>> ReadAsync(string path, CancellationToken ct)
        {
            if (!File.Exists(path)) return new();
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return await JsonSerializer.DeserializeAsync<List<MissedTradeRecord>>(fs, ReadOpts, ct)
                           .ConfigureAwait(false)
                       ?? new();
            }
            catch (JsonException)
            {
                // Keep the damaged file for inspection instead of silently overwriting it.
                try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { }
                return new();
            }
        }
    }
}
