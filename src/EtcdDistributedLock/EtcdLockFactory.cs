using dotnet_etcd;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using V3Electionpb;

namespace EtcdLock;

public class EtcdLockFactory : IEtcdLockFactory
{
    private readonly EtcdClient _etcdClient;
    private readonly ILogger _logger;

    public EtcdLockFactory(EtcdClient etcdClient, ILogger<EtcdLockFactory> logger)
    {
        _etcdClient = etcdClient ?? throw new ArgumentNullException(nameof(etcdClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<IEtcdLock?> AcquireAsync(string name, int lockTimeoutInSeconds = 10, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        EtcdLockLease? etcdLockLease = await EtcdLockLease.CreateAsync(_etcdClient, lockTimeoutInSeconds/*ms*/, cancellationToken, _logger)
            .ConfigureAwait(false);
        if (etcdLockLease == null)
        {
            return null;
        }

        try
        {
            using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationTokenSource.CancelAfter((lockTimeoutInSeconds * 1000) / 3); // 1/3 of the lease time

            CampaignResponse campaignResponse = await _etcdClient.CampaignAsync(
                new CampaignRequest
                {
                    Name = ByteString.CopyFromUtf8(name),
                    Lease = etcdLockLease.LeaseId,
                },
                cancellationToken: cancellationTokenSource.Token).ConfigureAwait(false);

            return new EtcdLock(_etcdClient, campaignResponse.Leader, etcdLockLease, _logger);
        }
        catch (OperationCanceledException)
        {
            await etcdLockLease.DisposeAsync()
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to acquire lock.");

            await etcdLockLease.DisposeAsync()
                .ConfigureAwait(false);
        }

        return null;
    }

    private class EtcdLock : IEtcdLock
    {
        private readonly EtcdLockLease _etcdLockLease;
        private readonly ILogger _logger;
        private readonly EtcdClient _etcdClient;
        private readonly LeaderKey _leaderKey;

        public EtcdLock(EtcdClient etcdClient, LeaderKey leaderKey, EtcdLockLease etcdLockLease, ILogger logger)
        {
            _etcdClient = etcdClient ?? throw new ArgumentNullException(nameof(etcdClient));
            _leaderKey = leaderKey ?? throw new ArgumentNullException(nameof(leaderKey));
            _etcdLockLease = etcdLockLease ?? throw new ArgumentNullException(nameof(etcdLockLease));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public CancellationToken CancellationToken => _etcdLockLease.CancellationToken;

        public async ValueTask DisposeAsync()
        {
            // 2. but if we want to revoke the campaign explicitly we can do it here
            try
            {
                await _etcdClient.ResignAsync(
                    new ResignRequest
                    {
                        Leader = _leaderKey,
                    }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Failed to revoke campaign.");
            }

            // 1. this will revoke the lease and cancel the keep alive task
            // no need to revoke the campaign explicitly
            await _etcdLockLease.DisposeAsync();
        }
    }
}
