namespace EtcdLock;

public interface IEtcdLockFactory
{
    ValueTask<IEtcdLock?> AcquireAsync(string name, int lockTimeoutInSeconds = 10, CancellationToken cancellationToken = default);
}
