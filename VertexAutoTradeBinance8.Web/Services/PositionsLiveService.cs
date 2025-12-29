using VertexAutoTradeBinance8.Web.Models;
using VertexAutoTradeBinance8.Web.Pages.Components;

namespace VertexAutoTradeBinance8.Web.Services;

public class PositionsLiveService
{
    [Flags]
    public enum PositionChange
    {
        None = 0,
        Mark = 1,
        Pnl = 2,
        Roi = 4,
        Risk = 8,
        All = 255
    }


    private readonly Dictionary<string, PositionVm> _positions = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _symbolIndex = new();
    // symbol -> positionKey


    public event Action<PositionVm>? OnPositionUpdated;

    public IReadOnlyCollection<PositionVm> GetAll()
    {
        lock (_lock)
            return _positions.Values.ToList();
    }
    // =========================================================
    // ACTIVE SYMBOLS (for WS subscriptions)
    // =========================================================
    public List<string> GetActiveSymbols()
    {
        lock (_lock)
        {
            return _positions.Values
                .Where(p => p != null && p.SizeUsdt > 0)
                .Select(p => p.Symbol)
                .Distinct()
                .ToList();
        }
    }
    public void Upsert(PositionVm vm)
    {
        lock (_lock)
        {
            _positions[vm.Key] = vm;
            _symbolIndex[vm.Symbol] = vm.Key;
        }

        vm.LastUpdate = DateTime.UtcNow;
        vm.ChangeMask = PositionChange.All;

        OnPositionUpdated?.Invoke(vm);
    }

    public void UpdateMark(string symbol, decimal mark)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        PositionVm? vm;

        lock (_lock)
        {
            if (!_symbolIndex.TryGetValue(symbol, out var key))
                return;

            if (!_positions.TryGetValue(key, out vm))
                return;

            var oldPnl = vm.Pnl;
            var oldRoi = vm.Roi;
            var oldMarginRatio = vm.MarginRatio;

            vm.Mark = mark;
            vm.ChangeMask = PositionChange.Mark;

            // === PnL / ROI ===
            if (vm.Entry > 0 && vm.SizeUsdt > 0)
            {
                var priceDiff = vm.Side == "LONG"
                    ? mark - vm.Entry
                    : vm.Entry - mark;

                vm.Pnl = priceDiff * (vm.SizeUsdt / vm.Entry);

                if (vm.Margin > 0)
                    vm.Roi = vm.Pnl / vm.Margin * 100m;
            }

            // === Margin Ratio (risk) ===
            if (vm.LiqPrice > 0 && vm.Mark > 0 && vm.Entry > 0)
            {
                var distToLiq = Math.Abs(vm.Mark - vm.LiqPrice);
                var distEntryToLiq = Math.Abs(vm.Entry - vm.LiqPrice);

                if (distEntryToLiq > 0)
                {
                    vm.MarginRatio = Math.Clamp(
                        Math.Round((1 - distToLiq / distEntryToLiq) * 100m, 2),
                        0m,
                        100m
                    );
                }
            }

            // === delta flags ===
            if (vm.Pnl != oldPnl) vm.ChangeMask |= PositionChange.Pnl;
            if (vm.Roi != oldRoi) vm.ChangeMask |= PositionChange.Roi;
            if (vm.MarginRatio != oldMarginRatio) vm.ChangeMask |= PositionChange.Risk;

            vm.LastUpdate = DateTime.UtcNow;
        }

        OnPositionUpdated?.Invoke(vm);
    }

    public void Remove(string symbol)
    {
        lock (_lock)
        {
            _positions.Remove(symbol);
        }
    }



    // === MOCK TIMER (пока вместо WS) ===
    public void StartMock()
    {
        var timer = new System.Threading.Timer(_ =>
        {
            lock (_lock)
            {
                foreach (var p in _positions.Values)
                {
                    // имитация движения цены
                    var delta = (decimal)(Random.Shared.NextDouble() - 0.5) * 0.5m;

                    p.Mark += delta;
                    p.Pnl += delta * 0.2m;
                    p.Roi = p.Margin > 0 ? (p.Pnl / p.Margin) * 100 : 0;
                }
            }

            foreach (var p in _positions.Values)
                OnPositionUpdated?.Invoke(p);

        }, null, 1000, 1000);
    }
}
