//using Microsoft.Extensions.Hosting;
//using VertexAutoTradeBinance8.Services;

//namespace VertexAutoTradeBinance8.Web.Services;

//public sealed class AiPulseBackgroundUpdater : BackgroundService
//{
//    private readonly AiSelfLearningService _learning;
//    private readonly IAiPulseEngine _pulse;

//    public AiPulseBackgroundUpdater(
//        AiSelfLearningService learning,
//        IAiPulseEngine pulse)
//    {
//        _learning = learning;
//        _pulse = pulse;
//    }

//    protected override async Task ExecuteAsync(CancellationToken ct)
//    {
//		Console.WriteLine("[PULSE] BackgroundUpdater STARTED");
//        while (!ct.IsCancellationRequested)
//        {
//            try
//            {
//                var states = _learning.GetRecentStates(120);
//				 Console.WriteLine($"[PULSE] states count = {states.Count}");

//                if (states.Count > 0)
//                {
//                    _pulse.Update(states);
//                }
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Pulse updater error: {ex}");
//            }

//            await Task.Delay(1000, ct);
//        }
//    }
//}
using VertexAutoTradeBinance8.Services;

public sealed class AiPulseBackgroundUpdater : BackgroundService
{
    private readonly IAiPulseEngine _engine;

    public AiPulseBackgroundUpdater(
        AiSelfLearningService learning,
        IAiPulseEngine engine
)
    {
        _engine = engine;
        learning.MarketStateCreated += engine.UpdateSingle;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.CompletedTask;
}
