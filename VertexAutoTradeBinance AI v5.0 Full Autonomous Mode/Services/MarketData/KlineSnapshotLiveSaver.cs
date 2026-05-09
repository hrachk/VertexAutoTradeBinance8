namespace VertexAutoTradeBinance8.Services.Bootstrap
{
    public sealed class KlineSnapshotLiveSaver : BackgroundService
    {
        private readonly KlineBufferPersistence _persistence;
        private readonly ILogger<KlineSnapshotLiveSaver> _logger;

        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);

        public KlineSnapshotLiveSaver(
            KlineBufferPersistence persistence,
            ILogger<KlineSnapshotLiveSaver> logger)
        {
            _persistence = persistence;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // дать системе стартануть
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Interval, ct);
                    await _persistence.SaveAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // shutdown — нормально
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[BOOT] Live kline snapshot save failed (non-fatal)");
                }
            }
        }
    }
}
