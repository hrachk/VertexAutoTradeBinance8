using Microsoft.Extensions.Hosting;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class AiPulseBackgroundUpdater : BackgroundService
{
    private readonly AiSelfLearningService _learning;
    private readonly IAiPulseEngine _pulse;

    public AiPulseBackgroundUpdater(
        AiSelfLearningService learning,
        IAiPulseEngine pulse)
    {
        _learning = learning;
        _pulse = pulse;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var states = _learning.GetRecentStates(120);

                if (states.Count > 0)
                {
                    _pulse.Update(states);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pulse updater error: {ex}");
            }

            await Task.Delay(1000, ct);
        }
    }
}
