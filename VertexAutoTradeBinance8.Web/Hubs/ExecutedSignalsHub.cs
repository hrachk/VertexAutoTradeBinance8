using Microsoft.AspNetCore.SignalR;
using VertexAutoTradeBinance8.Services;

public class ExecutedSignalsHub : Hub
{
    private static bool _hooked;

    public ExecutedSignalsHub()
    {
        if (!_hooked)
        {
            ExecutedSignalService.ExecutedSignalsChanged += OnChanged;
            _hooked = true;
        }
    }

    private void OnChanged()
    {
        Clients.All.SendAsync("ExecutedSignalsUpdated");
    }
}
