using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Pages.Components;

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
        while (!stoppingToken.IsCancellationRequested)
        {
            var all = _learning.GetRecentStates(300);

            var newStates = all
                .Where(s => s.Time > _lastProcessed)
                .OrderBy(s => s.Time)
                .ToList();

            if (newStates.Count > 0)
            {
                Console.WriteLine($"[PULSE FEED] new states = {newStates.Count}");

                _pulse.Update(newStates);

                // КРИТИЧЕСКОЕ: обновляем UI
                var market = _pulse.Market;
                _ui.Update(market);

                Console.WriteLine($"[UI UPDATED] pulse={market.SmoothedPulse:F4}");

                _lastProcessed = newStates.Max(s => s.Time);
            }

            await Task.Delay(250, stoppingToken);
        }
    }


}
