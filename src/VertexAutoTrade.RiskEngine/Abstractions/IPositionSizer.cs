namespace VertexAutoTrade.RiskEngine.Abstractions;

public interface IPositionSizer
{
    PositionSizeResult Calculate(PositionSizeRequest request);
}
