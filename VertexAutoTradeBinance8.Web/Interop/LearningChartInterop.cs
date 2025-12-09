using Microsoft.JSInterop;
using VertexAutoTradeBinance8.Models;

public sealed class LearningChartInterop
{
    private readonly IJSRuntime _js;

    public LearningChartInterop(IJSRuntime js)
    {
        _js = js;
    }

    [JSInvokable]
    public Task RenderLearningChart(List<AiLearningRecord> points)
    {
        return _js.InvokeVoidAsync("VertexLearningChart.renderData", points).AsTask();
    }
}
