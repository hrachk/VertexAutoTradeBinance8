using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// Снимок позиций и баланса аккаунта с коротким TTL.
///
/// FIX: раньше на каждый символ в каждом цикле делалось 3–12 вызовов
/// GetPositionInformation (weight 5) + ретраи 10×200мс для пустых символов →
/// 2+ секунды простоя на символ и риск бана по weight-limit.
/// Теперь — ОДИН запрос по всем символам раз в ~2 секунды, инвалидация после ордеров.
///
/// Fail-closed: если биржа не ответила, <see cref="IsFresh"/> = false и новые входы запрещены.
/// </summary>
public class AccountStateCache
{
    private readonly ILogger<AccountStateCache> _logger;
    private readonly BinanceClientFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BinancePositionDetailsUsdt[] _positions = Array.Empty<BinancePositionDetailsUsdt>();
    private DateTime _positionsAt = DateTime.MinValue;

    private decimal _walletBalance;
    private decimal _availableBalance;
    private DateTime _balanceAt = DateTime.MinValue;

    private static readonly TimeSpan PositionsTtl = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BalanceTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    public AccountStateCache(ILogger<AccountStateCache> logger, BinanceClientFactory factory)
    {
        _logger = logger;
        _factory = factory;
    }

    /// <summary>true если последний успешный снимок позиций моложе 30 с.</summary>
    public bool IsFresh => DateTime.UtcNow - _positionsAt < StaleAfter;

    public decimal WalletBalance => _walletBalance;
    public decimal AvailableBalance => _availableBalance;

    public void Invalidate()
    {
        _positionsAt = DateTime.MinValue;
        _balanceAt = DateTime.MinValue;
    }

    /// <summary>Все позиции с ненулевым объёмом (все символы).</summary>
    public async Task<IReadOnlyList<BinancePositionDetailsUsdt>> GetOpenPositionsAsync(CancellationToken ct, bool forceRefresh = false)
    {
        await RefreshPositionsAsync(ct, forceRefresh);
        return _positions.Where(p => p.Quantity != 0m).ToList();
    }

    public async Task<BinancePositionDetailsUsdt?> GetPositionAsync(string symbol, PositionSide side, CancellationToken ct, bool forceRefresh = false)
    {
        await RefreshPositionsAsync(ct, forceRefresh);
        return _positions.FirstOrDefault(p =>
            string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
            p.PositionSide == side &&
            p.Quantity != 0m);
    }

    public async Task<bool> HasAnyPositionAsync(string symbol, CancellationToken ct)
    {
        await RefreshPositionsAsync(ct, false);
        return _positions.Any(p =>
            string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && p.Quantity != 0m);
    }

    public async Task<int> CountOpenPositionsAsync(CancellationToken ct)
    {
        await RefreshPositionsAsync(ct, false);
        return _positions.Count(p => p.Quantity != 0m);
    }

    public async Task<(decimal wallet, decimal available)> GetBalancesAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && DateTime.UtcNow - _balanceAt < BalanceTtl)
            return (_walletBalance, _availableBalance);

        try
        {
            var client = _factory.CreateRestClient();
            var acc = await client.UsdFuturesApi.Account.GetBalancesAsync(ct: ct);
            if (acc.Success && acc.Data != null)
            {
                var usdt = acc.Data.FirstOrDefault(x => x.Asset == "USDT");
                if (usdt != null)
                {
                    _walletBalance = usdt.WalletBalance;
                    _availableBalance = usdt.AvailableBalance;
                    _balanceAt = DateTime.UtcNow;
                }
            }
            else
            {
                _logger.LogWarning("[ACCOUNT] balance fetch failed: {err}", acc.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ACCOUNT] balance fetch exception");
        }

        return (_walletBalance, _availableBalance);
    }

    private async Task RefreshPositionsAsync(CancellationToken ct, bool force)
    {
        if (!force && DateTime.UtcNow - _positionsAt < PositionsTtl)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if (!force && DateTime.UtcNow - _positionsAt < PositionsTtl)
                return;

            var client = _factory.CreateRestClient();
            var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
            if (res.Success && res.Data != null)
            {
                _positions = res.Data;
                _positionsAt = DateTime.UtcNow;
            }
            else
            {
                _logger.LogWarning("[ACCOUNT] positions fetch failed: {err}", res.Error);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ACCOUNT] positions fetch exception");
        }
        finally
        {
            _gate.Release();
        }
    }
}
