using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.Interface;

namespace VertexAutoTradeBinance8.Services;

public sealed class EngineStateOpenPositionProvider : IOpenPositionProvider
{
    private readonly EngineState _state;

    public EngineStateOpenPositionProvider(EngineStateSnapshotService stateSvc)
    {
        _state = stateSvc.State;
    }

    public bool HasOpenPosition(string symbol)
    {
        var key = EngineState.Key(symbol);

        if (!_state.Symbols.TryGetValue(key, out var st))
            return false;

        // КАНОНИЧЕСКИЙ КРИТЕРИЙ:
        // если недавно была защита — позиция жива или только что жила
        return st.LastProtectionUtc > DateTime.UtcNow.AddMinutes(-30)
               || st.LastHarvestUtc > DateTime.UtcNow.AddMinutes(-30);
    }
}
