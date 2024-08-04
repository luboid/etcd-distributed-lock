using dotnet_etcd;
using Etcdserverpb;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EtcdLock;

public class EtcdLockLease : IAsyncDisposable
{
    private readonly EtcdClient _etcdClient;
    private readonly long _leaseId;
    private readonly long _ttlInSeconds;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _keepAlive;

    private EtcdLockLease(EtcdClient etcdClient, long leaseId, long ttlInSeconds, CancellationToken cancellationToken, ILogger logger)
    {
        _etcdClient = etcdClient;
        _leaseId = leaseId;
        _ttlInSeconds = ttlInSeconds;
        _logger = logger;
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);


        int keepAliveTimeout = (int)_ttlInSeconds * 1000 / 3;
        int communicationTimeout = keepAliveTimeout / 2;

        _keepAlive = etcdClient.LeaseKeepAlive(_cancellationTokenSource, leaseId, keepAliveTimeout);
    }

    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    public long LeaseId => _leaseId;

    public async ValueTask DisposeAsync()
    {
        if (!_cancellationTokenSource.IsCancellationRequested)
        {
            await _cancellationTokenSource.CancelAsync()
                .ConfigureAwait(false);
        }

        try
        {
            await _keepAlive;
        }
        catch (OperationCanceledException)
        {
            // operation was cancelled
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) 
        {
            // operation was cancelled
        }
        catch (Exception ex)
        {
            _logger?.LogTrace(ex, "Unexpected exception while waiting keep alive task to end.");
        }

        _cancellationTokenSource.Dispose();

        // or we can live it to expire on its own
        try
        {
            await _etcdClient.LeaseRevokeAsync(
                new LeaseRevokeRequest
                {
                    ID = _leaseId,
                }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogTrace(ex, "Unexpected exception while revoking lease.");
        }
    }

    public static async ValueTask<EtcdLockLease?> CreateAsync(EtcdClient etcdClient, int timeToLiveInSeconds, CancellationToken cancellationToken, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(etcdClient);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationTokenSource.CancelAfter((timeToLiveInSeconds * 1000) / 3); // 1/3 of the lease time

            LeaseGrantResponse leaseGrantResponse = await etcdClient.LeaseGrantAsync(
                new LeaseGrantRequest
                {
                    TTL = timeToLiveInSeconds, // seconds
                },
                cancellationToken: cancellationTokenSource.Token)
                    .ConfigureAwait(false);

            return new EtcdLockLease(etcdClient, leaseGrantResponse.ID, leaseGrantResponse.TTL, cancellationToken, logger);
        }
        catch (OperationCanceledException)
        {
            // operation was cancelled
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "Failed to create lease.");
        }

        return null;
    }
}
