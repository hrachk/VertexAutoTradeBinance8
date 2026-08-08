using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services.Storage;

/// <summary>
/// Журнал решений: append-only, с потолком по количеству записей.
///
/// ЧТО БЫЛО НЕ ТАК В RiskManager.LogMissedTrade
///   1. На каждый отказ файл читался целиком, десериализовался в List&lt;object&gt;
///      и переписывался целиком. При тысяче записей это заметная пауза
///      прямо в торговом цикле.
///   2. File.WriteAllText поверх живого файла: падение процесса или
///      одновременное чтение из Web оставляли обрезанный JSON, после чего
///      файл не разбирался уже никогда.
///   3. Никакого потолка — файл рос до бесконечности.
///   4. catch { } — если запись не удалась, об этом никто не узнавал.
///
/// Здесь: запись под блокировкой, потолок с обрезкой самых старых,
/// атомарная подмена файла и явное логирование ошибок.
/// </summary>
public sealed class TradeDecisionJournal
{
    private const int MaxRecords = 4000;
    private const int TrimTo = 3000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly VertexPaths _paths;
    private readonly ILogger<TradeDecisionJournal> _logger;
    private readonly object _gate = new();

    private List<TradeDecision>? _cache;

    /// <summary>Поднимается после каждой записи — Web использует для живого обновления.</summary>
    public static event Action? Changed;

    public TradeDecisionJournal(VertexPaths paths, ILogger<TradeDecisionJournal> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public void Record(TradeDecision decision)
    {
        lock (_gate)
        {
            try
            {
                var list = LoadUnsafe();
                list.Add(decision);

                if (list.Count > MaxRecords)
                    list = list.Skip(list.Count - TrimTo).ToList();

                VertexPaths.WriteAtomic(_paths.Decisions, JsonSerializer.Serialize(list, JsonOpts));
                _cache = list;
            }
            catch (Exception ex)
            {
                // Раньше здесь стоял пустой catch — потеря журнала проходила молча
                _logger.LogError(ex, "[JOURNAL] не удалось записать решение по {symbol}", decision.Symbol);
                return;
            }
        }

        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Дописывает результат исполнения к уже записанному решению.</summary>
    public void Complete(string id, long? orderId, decimal? filledPrice, decimal? placedStop)
    {
        lock (_gate)
        {
            try
            {
                var list = LoadUnsafe();
                var rec = list.LastOrDefault(x => x.Id == id);
                if (rec == null) return;

                rec.OrderId = orderId;
                rec.FilledPrice = filledPrice;
                rec.PlacedStop = placedStop;

                VertexPaths.WriteAtomic(_paths.Decisions, JsonSerializer.Serialize(list, JsonOpts));
                _cache = list;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JOURNAL] не удалось дополнить решение {id}", id);
                return;
            }
        }

        try { Changed?.Invoke(); } catch { }
    }

    public List<TradeDecision> Load()
    {
        lock (_gate)
            return LoadUnsafe().ToList();
    }

    private List<TradeDecision> LoadUnsafe()
    {
        if (_cache != null)
            return _cache;

        var json = VertexPaths.ReadOrNull(_paths.Decisions);

        if (string.IsNullOrWhiteSpace(json))
            return _cache = new List<TradeDecision>();

        try
        {
            _cache = JsonSerializer.Deserialize<List<TradeDecision>>(json, JsonOpts) ?? new();
        }
        catch (JsonException ex)
        {
            // Битый файл не должен останавливать торговлю: отводим его в сторону
            _logger.LogError(ex, "[JOURNAL] decisions.json не разобран — файл отложен как .corrupt");

            try { File.Move(_paths.Decisions, _paths.Decisions + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}"); }
            catch { }

            _cache = new List<TradeDecision>();
        }

        return _cache;
    }
}
