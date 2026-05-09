namespace VertexAutoTradeBinance8.Services.DecisionTrace
{
    public interface IDecisionTraceService
    {
        void Record(DecisionTraceSnapshot snapshot);
    }
}
