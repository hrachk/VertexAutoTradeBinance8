using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Services.Recovery;

namespace VertexAutoTradeBinance8.Services.Bootstrap;

public sealed class SupervisorBootstrapHostedService : BackgroundService
{
    private readonly ILogger<SupervisorBootstrapHostedService> _logger;
    private readonly IPositionRecoveryService _recovery;
    private readonly IBootGate _gate;

    public SupervisorBootstrapHostedService(
        ILogger<SupervisorBootstrapHostedService> logger,
        IPositionRecoveryService recovery,
        IBootGate gate)
    {
        _logger = logger;
        _recovery = recovery;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogWarning("[BOOT] Supervisor bootstrap starting (recovery-first) ...");
            await _recovery.ReconcileAndProtectAsync(stoppingToken);
            _logger.LogWarning("[BOOT] Supervisor bootstrap finished OK.");
        }
        catch (Exception ex)
        {
            // Даже если recovery упал — дальше можно жить, но gate всё равно открываем,
            // иначе бот никогда не стартанёт.
            _logger.LogError(ex, "[BOOT] Supervisor bootstrap FAILED (continuing).");
        }
        finally
        {
            _gate.MarkReady();
            _logger.LogWarning("[BOOT] BootGate READY.");
        }
    }
}
