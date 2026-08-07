public interface IOrderDispatcher
{
    /// <summary>Ставит действие в очередь. false = очередь переполнена, действие НЕ будет выполнено.</summary>
    bool Enqueue(Func<CancellationToken, Task> orderAction);
}
