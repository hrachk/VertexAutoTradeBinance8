namespace VertexAutoTradeBinance8.Services.Interface
{
    public interface IOpenPositionProvider
    {
        bool HasOpenPosition(string symbol);
    }

}
