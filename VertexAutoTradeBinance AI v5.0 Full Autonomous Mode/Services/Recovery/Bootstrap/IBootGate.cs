namespace VertexAutoTradeBinance8.Services.Bootstrap;

public interface IBootGate
{
    bool IsReady { get; }
    Task WaitReadyAsync(CancellationToken ct);
    void MarkReady();
}
