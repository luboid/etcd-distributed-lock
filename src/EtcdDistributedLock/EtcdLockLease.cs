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
    private readonly CancellationToken _cancellationToken;
    private readonly Task _keepAlive;

    private EtcdLockLease(EtcdClient etcdClient, long leaseId, long ttlInSeconds, CancellationToken cancellationToken, ILogger logger)
    {
        _etcdClient = etcdClient;
        _leaseId = leaseId;
        _ttlInSeconds = ttlInSeconds;
        _logger = logger;
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationToken = _cancellationTokenSource.Token;

        _keepAlive = Task.Factory.StartNew(
            (_) => KeepAlive(),
            null,
            _cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Current)
            .Unwrap();
    }

    public CancellationToken CancellationToken => _cancellationToken;

    public long LeaseId => _leaseId;

    private async Task KeepAlive()
    {
        int timeToCheckInMilliseconds = (int)((_ttlInSeconds * 1000) / 3);
        LeaseKeepAliveRequest request = new()
        {
            ID = _leaseId
        };

        try
        {
            try
            {
                using AsyncDuplexStreamingCall<LeaseKeepAliveRequest, LeaseKeepAliveResponse> leaser =
                    _etcdClient.LeaseKeepAlive(cancellationToken: _cancellationToken);

                while (!_cancellationToken.IsCancellationRequested)
                {
                    await leaser.RequestStream.WriteAsync(request)
                        .ConfigureAwait(false);

                    // new CancellationTokenSource is created to cancel the task with timeout
                    if (await leaser.ResponseStream.MoveNext(_cancellationToken)
                        .ConfigureAwait(false))
                    {
                        LeaseKeepAliveResponse update = leaser.ResponseStream.Current;
                        if (update.ID != _leaseId || update.TTL == 0) // expired
                        {
                            await _cancellationTokenSource.CancelAsync()
                                .ConfigureAwait(false);

                            await leaser.RequestStream.CompleteAsync()
                                .ConfigureAwait(false);

                            break;
                        }
                    }
                    else
                    {
                        await _cancellationTokenSource.CancelAsync()
                            .ConfigureAwait(false);

                        await leaser.RequestStream.CompleteAsync()
                            .ConfigureAwait(false);

                        break;
                    }


                    await Task.Delay(timeToCheckInMilliseconds, _cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                await _cancellationTokenSource.CancelAsync()
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // operation was cancelled
        }
        catch (Exception ex)
        {
            _logger?.LogTrace(ex, "Unexpected exception while keeping lease live.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellationTokenSource.CancelAsync()
            .ConfigureAwait(false);

        try
        {
            await _keepAlive;
        }
        catch (Exception ex)
        {
            _logger?.LogTrace(ex, "Unexpected exception while revoking lease.");
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

    public static async ValueTask<EtcdLockLease?> CreateAsync(EtcdClient etcdClient, int lockTimeoutInSeconds, CancellationToken cancellationToken, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(etcdClient);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationTokenSource.CancelAfter((lockTimeoutInSeconds * 1000) / 3); // 1/3 of the lease time

            LeaseGrantResponse leaseGrantResponse = await etcdClient.LeaseGrantAsync(
                new LeaseGrantRequest
                {
                    TTL = lockTimeoutInSeconds, // seconds
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
