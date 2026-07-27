using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using IssueTracker.Core.Interfaces;
using IssueTracker.Infrastructure.Data;
using IssueTracker.API.Protos;

namespace IssueTracker.Infrastructure.Services;

public class OutboxProcessorService : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("IssueTracker.OutboxProcessor");
    private readonly IServiceScopeFactory serviceScopeFactory;

    public OutboxProcessorService(IServiceScopeFactory serviceScopeFactory)
    {
        this.serviceScopeFactory = serviceScopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IOutboxRepository repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            var logger = scope.ServiceProvider.GetRequiredService<ActivityLogger.ActivityLoggerClient>();

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
                            using var doc = System.Text.Json.JsonDocument.Parse(message.Content);
                            var root = doc.RootElement;
                            var issueId = root.GetProperty("IssueId").ToString();
                            var action = root.GetProperty("Action").GetString() ?? "Updated";
                            var userEmail = root.TryGetProperty("UserEmail", out var emailProp) ? emailProp.GetString() : "System / Anonymous";

                            using var activity = ActivitySource.StartActivity("Outbox.LogActivity", ActivityKind.Client);
                            activity?.SetTag("issue.id", issueId);
                            activity?.SetTag("issue.action", action);

                            await logger.LogActivityAsync(new ActivityRequest
                            {
                                IssueId = issueId,
                                Action = action,
                                UserEmail = userEmail ?? "System / Anonymous",
                                Timestamp = DateTime.UtcNow.ToString("O")
                            });
                            await repo.MarkAsProcessedAsync(message);
                        }
                        await transaction.CommitAsync(ct);
                    }
                    catch
                    {
                        await transaction.RollbackAsync(ct);
                    }
                    return true;
                },
                null,
                stoppingToken);

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }
}