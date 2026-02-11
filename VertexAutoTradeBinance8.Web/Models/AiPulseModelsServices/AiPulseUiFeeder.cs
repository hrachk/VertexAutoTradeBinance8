using VertexAutoTradeBinance8.Services;

public sealed class AiPulseUiFeeder : BackgroundService
{
    private readonly IAiPulseEngine _pulse;
    private readonly AiPulseUiState _ui;
    private readonly AiSelfLearningService _ai;

    public AiPulseUiFeeder(
        IAiPulseEngine pulse,
        AiPulseUiState ui,
        AiSelfLearningService ai)
    {
        _pulse = pulse;
        _ui = ui;
        _ai = ai;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var states = _ai.GetRecentStates();
            _pulse.Update(states);

            _ui.Update(_pulse.Market);

            await Task.Delay(250, stoppingToken);
        }
    }
}
