using Microsoft.AspNetCore.SignalR;
using VertexAutoTradeBinance8.Web.Hubs;

public class ExecutedSignalsPushService
{
    private readonly IHubContext<ExecutedSignalsHub> _hub;

    public ExecutedSignalsPushService(IHubContext<ExecutedSignalsHub> hub)
    {
        _hub = hub;
    }

    public async Task PushUpdateAsync()
    {
        await _hub.Clients.All.SendAsync("ExecutedSignalsUpdated");
    }
}
