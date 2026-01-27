using VertexAutoTradeBinance8.Services.DTO.Debug;

public sealed class DecisionMarkersPersistenceHostedService : BackgroundService
{
    private readonly DecisionMarkersFileService _svc;
    private readonly ILogger<DecisionMarkersPersistenceHostedService> _logger;

    public DecisionMarkersPersistenceHostedService(
        DecisionMarkersFileService svc,
        ILogger<DecisionMarkersPersistenceHostedService> logger)
    {
        _svc = svc;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 1️⃣ restore once
        await _svc.RestoreAsync(ct);

        // 2️⃣ periodic save (live mode)
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                await _svc.SaveAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DEBUG] DecisionMarkers save loop failed");
            }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        // 3️⃣ final flush
        await _svc.SaveAsync(ct);
        await base.StopAsync(ct);
    }
}
