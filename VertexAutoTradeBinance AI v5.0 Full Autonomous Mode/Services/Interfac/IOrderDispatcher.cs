public interface IOrderDispatcher
{
    void Enqueue(Func<CancellationToken, Task> orderAction);
}
