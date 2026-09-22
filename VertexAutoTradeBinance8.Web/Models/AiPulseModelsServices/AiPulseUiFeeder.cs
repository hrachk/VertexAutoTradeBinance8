using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Pages.Components;

/// <summary>
/// Feeds AI pulse into UI state. Poll interval intentionally slow:
/// 250ms caused sustained Web CPU ~30%+ with GetRecentStates(300) every tick.
/// </summary>
public sealed class AiPulseUiFeeder : BackgroundService
{
    private readonly IAiPulseEngine _pulse;
    private readonly AiPulseUiState _ui;
    private readonly AiSelfLearningService _learning;
    private DateTime _lastProcessed = DateTime.MinValue;

    public AiPulseUiFeeder(
        IAiPulseEngine pulse,
        AiPulseUiState ui,
        AiSelfLearningService learning)
    {
        _pulse = pulse;
        _ui = ui;
        _learning = learning;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var all = _learning.GetRecentStates(80);
                var newStates = all
                    .Where(s => s.Time > _lastProcessed)
                    .OrderBy(s => s.Time)
                    .ToList();

                if (newStates.Count > 0)
                {
                    _pulse.Update(newStates);
                    _ui.Update(_pulse.Market);
                    _lastProcessed = newStates.Max(s => s.Time);
                }
            }
            catch { }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
