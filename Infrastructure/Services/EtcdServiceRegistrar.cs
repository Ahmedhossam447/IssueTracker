using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using dotnet_etcd;
using Etcdserverpb;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IssueTracker.Infrastructure.Services;

/// <summary>
/// DDIA Ch. 10 (Coordination Service): Ephemeral Service Registration with Lease Heartbeats.
/// Registers this .NET API instance under /services/api/{ip}:8080 so the Go Edge Proxy
/// discovers it instantaneously via etcd Watch notifications instead of 10s DNS polling.
/// </summary>
public class EtcdServiceRegistrar : BackgroundService
{
    private const string ServicePrefix = "/services/api/";
    private const int LeaseTtlSeconds = 5;

    private readonly EtcdClient _etcdClient;
    private readonly ILogger<EtcdServiceRegistrar> _logger;

    public EtcdServiceRegistrar(EtcdClient etcdClient, ILogger<EtcdServiceRegistrar> logger)
    {
        _etcdClient = etcdClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ip = GetLocalIPv4Address();
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080";
        var key = $"{ServicePrefix}{ip}:{port}";
        var value = $"http://{ip}:{port}";

        while (!stoppingToken.IsCancellationRequested)
        {
            long leaseId = 0;
            try
            {
                var lease = await _etcdClient.LeaseGrantAsync(
                    new LeaseGrantRequest { TTL = LeaseTtlSeconds },
                    cancellationToken: stoppingToken);
                leaseId = lease.ID;

                await _etcdClient.PutAsync(new PutRequest
                {
                    Key = Google.Protobuf.ByteString.CopyFromUtf8(key),
                    Value = Google.Protobuf.ByteString.CopyFromUtf8(value),
                    Lease = leaseId
                }, cancellationToken: stoppingToken);

                _logger.LogInformation("[etcd-Discovery] Registered ephemeral node {Key} -> {Value} (Lease={LeaseId}, TTL={Ttl}s)",
                    key, value, leaseId, LeaseTtlSeconds);

                // Keep the lease alive until shutdown or connection loss
                await _etcdClient.LeaseKeepAlive(leaseId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown: revoke lease immediately so Proxy removes us with zero 502s
                if (leaseId != 0)
                {
                    try
                    {
                        await _etcdClient.LeaseRevokeAsync(new LeaseRevokeRequest { ID = leaseId });
                        _logger.LogInformation("[etcd-Discovery] Deregistered {Key} on graceful shutdown.", key);
                    }
                    catch { /* Best-effort */ }
                }
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[etcd-Discovery] Registration error for {Key}. Retrying in 2s...", key);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private static string GetLocalIPv4Address()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        var ip = host.AddressList.FirstOrDefault(a =>
            a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        return ip?.ToString() ?? "127.0.0.1";
    }
}
