namespace EtcdLock;

public interface IEtcdLockFactory
{
    ValueTask<IEtcdLock?> AcquireAsync(string name, int timeToLiveInSeconds = 10, CancellationToken cancellationToken = default);
}
