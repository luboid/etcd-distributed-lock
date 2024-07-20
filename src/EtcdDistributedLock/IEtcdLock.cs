namespace EtcdLock;

public interface IEtcdLock : IAsyncDisposable
{
    CancellationToken CancellationToken
    {
        get;
    }
}
