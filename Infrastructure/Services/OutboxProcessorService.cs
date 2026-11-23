using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using IssueTracker.Core.Interfaces;
using IssueTracker.Infrastructure.Data;
using IssueTracker.API.Protos;
using Mvccpb;

namespace IssueTracker.Infrastructure.Services;

public class OutboxProcessorService : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("IssueTracker.OutboxProcessor");
    private const string LeaderKey = "/locks/outbox/leader";
    private const int LeaseTtlSeconds = 10;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly EtcdClient _etcdClient;
    private readonly ILogger<OutboxProcessorService> _logger;
    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid().ToString()[..6]}";

    public OutboxProcessorService(
        IServiceScopeFactory serviceScopeFactory,
        EtcdClient etcdClient,
        ILogger<OutboxProcessorService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _etcdClient = etcdClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Grant an etcd Lease (ephemeral node TTL = 10s)
                var lease = await _etcdClient.LeaseGrantAsync(
                    new LeaseGrantRequest { TTL = LeaseTtlSeconds },
                    cancellationToken: stoppingToken);

                // 2. Atomic Compare-And-Swap (CAS) Leader Election via etcd Txn:
                //    Only succeed if CreateRevision("/locks/outbox/leader") == 0 (no current leader)
                var keyBytes = ByteString.CopyFromUtf8(LeaderKey);
                var valBytes = ByteString.CopyFromUtf8(_instanceId);

                var txnRequest = new TxnRequest();
                txnRequest.Compare.Add(new Compare
                {
                    Key = keyBytes,
                    Target = Compare.Types.CompareTarget.Create,
                    Result = Compare.Types.CompareResult.Equal,
                    CreateRevision = 0
                });
                txnRequest.Success.Add(new RequestOp
                {
                    RequestPut = new PutRequest
                    {
                        Key = keyBytes,
                        Value = valBytes,
                        Lease = lease.ID
                    }
                });

                var txnResponse = await _etcdClient.TransactionAsync(txnRequest, cancellationToken: stoppingToken);

                if (txnResponse.Succeeded)
                {
                    // etcd's Raft Revision IS our strictly monotonic Fencing Token!
                    long fencingToken = txnResponse.Header.Revision;
                    _logger.LogInformation(
                        "[etcd-Leader] Replica {InstanceId} ELECTED as Outbox Leader! Lease={LeaseId}, FencingToken(ModRevision)={FencingToken}",
                        _instanceId, lease.ID, fencingToken);

                    await RunAsLeaderAsync(lease.ID, fencingToken, stoppingToken);
                }
                else
                {
                    // Another replica is already Leader! Revoke our unused lease and Watch for leader failure.
                    await _etcdClient.LeaseRevokeAsync(new LeaseRevokeRequest { ID = lease.ID }, cancellationToken: stoppingToken);
                    _logger.LogInformation("[etcd-Follower] Replica {InstanceId} standing by, watching {LeaderKey}...", _instanceId, LeaderKey);
                    await WaitForLeaderLossAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[etcd-Election] Error in Outbox leader loop for {InstanceId}. Retrying in 3s...", _instanceId);
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task RunAsLeaderAsync(long leaseId, long fencingToken, CancellationToken stoppingToken)
    {
        using var leaderCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // Background heartbeat to keep our etcd Lease alive while healthy
        var keepAliveTask = Task.Run(async () =>
        {
            try
            {
                await _etcdClient.LeaseKeepAlive(leaseId, leaderCts.Token);
            }
            catch (Exception ex) when (!leaderCts.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "[etcd-Leader] LeaseKeepAlive lost for {InstanceId}! Stepping down.", _instanceId);
                leaderCts.Cancel();
            }
        }, leaderCts.Token);

        try
        {
            int consecutiveFailures = 0;
            const int failureThreshold = 3;
            const double baseDelaySeconds = 3.0;
            const double maxBackoffSeconds = 30.0;

            while (!leaderCts.IsCancellationRequested)
            {
                bool succeeded = await ProcessOutboxBatchAsync(fencingToken, leaderCts.Token);
                TimeSpan delay;

                if (succeeded)
                {
                    if (consecutiveFailures >= failureThreshold)
                    {
                        _logger.LogInformation("[CircuitBreaker] Outbox circuit CLOSED (NotificationService recovered).");
                    }
                    consecutiveFailures = 0;
                    delay = TimeSpan.FromSeconds(baseDelaySeconds);
                }
                else
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= failureThreshold)
                    {
                        double expSeconds = Math.Min(maxBackoffSeconds, baseDelaySeconds * Math.Pow(2, consecutiveFailures - failureThreshold + 1));
                        int jitterMs = Random.Shared.Next(0, 1000);
                        delay = TimeSpan.FromSeconds(expSeconds) + TimeSpan.FromMilliseconds(jitterMs);
                        _logger.LogWarning(
                            "[CircuitBreaker] Outbox circuit OPEN ({Failures} consecutive gRPC failures). Backing off for {DelayMs}ms (with jitter).",
                            consecutiveFailures, (int)delay.TotalMilliseconds);
                    }
                    else
                    {
                        delay = TimeSpan.FromSeconds(baseDelaySeconds);
                    }
                }

                await Task.Delay(delay, leaderCts.Token);
            }
        }
        finally
        {
            leaderCts.Cancel();
            try
            {
                await _etcdClient.LeaseRevokeAsync(new LeaseRevokeRequest { ID = leaseId });
            }
            catch { /* Best-effort step-down */ }
            await Task.WhenAny(keepAliveTask, Task.Delay(500));
        }
    }

    private async Task WaitForLeaderLossAsync(CancellationToken stoppingToken)
    {
        var leaderLostTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // Watch /locks/outbox/leader for DELETE (crash / lease expiry / graceful step-down)
        var watchTask = _etcdClient.WatchAsync(LeaderKey, response =>
        {
            foreach (var ev in response.Events)
            {
                if (ev.Type == Event.Types.EventType.Delete)
                {
                    _logger.LogInformation("[etcd-Watch] Detected Outbox Leader loss! Waking up to compete for leadership...");
                    leaderLostTcs.TrySetResult();
                }
            }
        }, cancellationToken: watchCts.Token);

        // Safety fallback: also check once every 15s in case the key was already deleted before Watch attached
        await Task.WhenAny(leaderLostTcs.Task, Task.Delay(TimeSpan.FromSeconds(15), stoppingToken));
        watchCts.Cancel();
    }

    private async Task<bool> ProcessOutboxBatchAsync(long fencingToken, CancellationToken stoppingToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IOutboxRepository repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var logger = scope.ServiceProvider.GetRequiredService<ActivityLogger.ActivityLoggerClient>();

        bool hadNetworkFailure = false;
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            0,
            async (dbCtx, state, ct) =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
                try
                {
                    var messages = await repo.GetUnprocessedMessagesAsync();
                    foreach (var message in messages)
                    {
                        using var doc = JsonDocument.Parse(message.Content);
                        var root = doc.RootElement;
                        var issueId = root.GetProperty("IssueId").ToString();
                        var action = root.GetProperty("Action").GetString() ?? "Updated";
                        var userEmail = root.TryGetProperty("UserEmail", out var emailProp) ? emailProp.GetString() : "System / Anonymous";

                        using var activity = ActivitySource.StartActivity("Outbox.LogActivity", ActivityKind.Client);
                        activity?.SetTag("issue.id", issueId);
                        activity?.SetTag("issue.action", action);
                        activity?.SetTag("outbox.message_id", message.Id.ToString());
                        activity?.SetTag("etcd.fencing_token", fencingToken);

                        try
                        {
                            await logger.LogActivityAsync(new ActivityRequest
                            {
                                IssueId = issueId,
                                Action = action,
                                UserEmail = userEmail ?? "System / Anonymous",
                                Timestamp = DateTime.UtcNow.ToString("O"),
                                MessageId = message.Id.ToString(),
                                FencingToken = fencingToken
                            }, cancellationToken: ct);
                        }
                        catch
                        {
                            // Stop processing the rest of the batch on failure,
                            // but commit any earlier messages that were already acknowledged!
                            hadNetworkFailure = true;
                            break;
                        }

                        await repo.MarkAsProcessedAsync(message);
                    }
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(ct);
                    hadNetworkFailure = true;
                }
                return true;
            },
            null,
            stoppingToken);

        return !hadNetworkFailure;
    }
}
