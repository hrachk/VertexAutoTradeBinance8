using System.Collections.Concurrent;
using Binance.Net.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// Лимит входов по одному символу: не более MaxEntriesPerSymbol (по умолчанию 2).
/// При 2-м входе (добор) можно слегка увеличить объём.
/// </summary>
public class PositionGuardService
{
    private readonly ILogger<PositionGuardService> _logger;
    private readonly BinanceClientFactory _factory;
    private readonly int _maxEntries;

    /// <summary>Сколько успешных входов сделано, пока позиция жива (symbol → count).</summary>
    private readonly ConcurrentDictionary<string, int> _entryCount =
        new(StringComparer.OrdinalIgnoreCase);

    public PositionGuardService(
        ILogger<PositionGuardService> logger,
        BinanceClientFactory factory,
        IOptions<TradingOptions>? options = null)
    {
        _logger = logger;
        _factory = factory;
        var cfg = options?.Value?.MaxEntriesPerSymbol ?? 2;
        _maxEntries = cfg < 1 ? 1 : (cfg > 3 ? 3 : cfg); // clamp 1..3
    }

    public int MaxEntriesPerSymbol => _maxEntries;

    /// <summary>
    /// Есть ли активная позиция по символу (hedge: long или short).
    /// +1 Long, -1 Short, 0 flat.
    /// </summary>
    public async Task<int> GetCurrentPositionSideAsync(string symbol)
    {
        using var client = _factory.CreateRestClient();
        var result = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol);
        if (!result.Success || result.Data == null)
            return 0;

        decimal net = 0;
        foreach (var p in result.Data.Where(x =>
                     string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
        {
            net += p.Quantity;
        }

        if (net > 0) return 1;
        if (net < 0) return -1;
        return 0;
    }

    public async Task<decimal> GetOpenQtyAbsAsync(string symbol)
    {
        using var client = _factory.CreateRestClient();
        var result = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol);
        if (!result.Success || result.Data == null)
            return 0;

        return result.Data
            .Where(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .Sum(x => Math.Abs(x.Quantity));
    }

    /// <summary>
    /// Решение по входу.
    /// Block = true → не входить.
    /// IsAdd = true → это добор (2-й вход), QtyMultiplier > 1 для чуть большего объёма.
    /// </summary>
    public async Task<EntryDecision> EvaluateAsync(string symbol, bool wantLong, bool superSignal)
    {
        int posSide = await GetCurrentPositionSideAsync(symbol);
        int want = wantLong ? 1 : -1;

        // Позиции нет → полный вход, счётчик сбросим при RegisterEntry
        if (posSide == 0)
        {
            _entryCount.TryRemove(symbol, out _);
            return EntryDecision.AllowNew();
        }

        // Противоположная сторона — не усредняем в другую сторону здесь
        if (posSide != want)
        {
            _logger.LogInformation(
                "[GUARD][{symbol}] Block: open side={pos} signal side={want}",
                symbol, posSide > 0 ? "LONG" : "SHORT", want > 0 ? "LONG" : "SHORT");
            return EntryDecision.Block("OppositeSideOpen");
        }

        int count = _entryCount.GetOrAdd(symbol, 1); // уже есть поза → минимум 1 вход был

        // Если счётчик ещё 0, но поза есть (ручная / после рестарта) — считаем как 1
        if (count <= 0)
        {
            count = 1;
            _entryCount[symbol] = 1;
        }

        if (count >= _maxEntries)
        {
            _logger.LogInformation(
                "[GUARD][{symbol}] Block: max entries {max} reached (count={c})",
                symbol, _maxEntries, count);
            return EntryDecision.Block($"MaxEntries:{count}/{_maxEntries}");
        }

        // Добор разрешён (2-й вход). Чуть больше объём, если super — ещё чуть.
        decimal mult = superSignal ? 1.40m : 1.25m;
        _logger.LogInformation(
            "[GUARD][{symbol}] Allow ADD #{n}/{max} qty×{m}",
            symbol, count + 1, _maxEntries, mult);
        return EntryDecision.AllowAdd(mult);
    }

    /// <summary>После успешного ордера.</summary>
    public void RegisterSuccessfulEntry(string symbol)
    {
        _entryCount.AddOrUpdate(symbol, 1, (_, c) => c + 1);
        _logger.LogInformation("[GUARD][{symbol}] entry count → {c}",
            symbol, _entryCount.GetValueOrDefault(symbol));
    }

    /// <summary>Когда позиция закрыта (0 qty).</summary>
    public void ResetSymbol(string symbol)
    {
        if (_entryCount.TryRemove(symbol, out var c))
            _logger.LogInformation("[GUARD][{symbol}] reset entry count (was {c})", symbol, c);
    }

    /// <summary>Периодически сбрасывать счётчики по символам без позиции.</summary>
    public async Task SyncClosedPositionsAsync(IEnumerable<string> symbols)
    {
        foreach (var s in symbols)
        {
            if (await GetCurrentPositionSideAsync(s) == 0)
                ResetSymbol(s);
        }
    }

    [Obsolete("Use EvaluateAsync")]
    public async Task<bool> ShouldBlockAsync(string symbol, bool superSignal)
    {
        var d = await EvaluateAsync(symbol, wantLong: true, superSignal);
        // legacy: cannot know side — only block if any position and not super and max
        int side = await GetCurrentPositionSideAsync(symbol);
        if (side == 0) return false;
        if (superSignal)
        {
            int c = _entryCount.GetOrAdd(symbol, 1);
            return c >= _maxEntries;
        }
        return true;
    }
}

public sealed class EntryDecision
{
    public bool Block { get; init; }
    public bool IsAdd { get; init; }
    public decimal QtyMultiplier { get; init; } = 1m;
    public string Reason { get; init; } = "";

    public static EntryDecision AllowNew() => new() { Block = false, IsAdd = false, QtyMultiplier = 1m, Reason = "New" };
    public static EntryDecision AllowAdd(decimal mult) => new() { Block = false, IsAdd = true, QtyMultiplier = mult, Reason = "Add" };
    public static EntryDecision Block(string reason) => new() { Block = true, IsAdd = false, QtyMultiplier = 0m, Reason = reason };
}
