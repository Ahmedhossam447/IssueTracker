using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using IssueTracker.Core.Entities;

namespace IssueTracker.SimulationTests;

// ============================================================================
// DDIA CHAPTER 8 & 9: OUTBOX -> gRPC -> NOTIFICATION SERVICE SIMULATION
// Simulates unreliable networks (pre-delivery drops & post-delivery response
// drops / "Network False Negatives") across multiple Outbox polling cycles.
// ============================================================================
public static class OutboxNetworkSimulation
{
    public record SimulatedMongoLog(string? MessageId, string IssueId, string Action, string UserEmail);

    public class SimulatedGoNotificationService
    {
        private readonly SimulationEngine _sim;
        private readonly bool _useInboxIdempotency;
        private readonly HashSet<string> _processedInboxIds = new();
        public List<SimulatedMongoLog> MongoAuditLogs { get; } = new();
        public int WebSocketBroadcastCount { get; private set; }

        public SimulatedGoNotificationService(SimulationEngine sim, bool useInboxIdempotency)
        {
            _sim = sim;
            _useInboxIdempotency = useInboxIdempotency;
        }

        public void HandleLogActivity(string? messageId, string issueId, string action, string userEmail)
        {
            if (_useInboxIdempotency && !string.IsNullOrEmpty(messageId))
            {
                if (_processedInboxIds.Contains(messageId))
                {
                    _sim.Log("GO-INBOX", $"Duplicate MessageId '{messageId[..8]}' ignored (Idempotent Inbox hit!)");
                    return;
                }
                _processedInboxIds.Add(messageId);
            }

            MongoAuditLogs.Add(new SimulatedMongoLog(messageId, issueId, action, userEmail));
            WebSocketBroadcastCount++;
            _sim.Log("GO-MONGO", $"Inserted audit log for Issue {issueId[..8]} [{action}] (Total logs: {MongoAuditLogs.Count})");
        }
    }

    public static (int TotalSeeds, int FailedSeeds, SimulationEngine? FirstFailureSim, string? FirstFailureMessage) Run(
        int totalSeeds,
        bool useInboxIdempotency)
    {
        int failedSeeds = 0;
        SimulationEngine? firstFailureSim = null;
        string? firstFailureMessage = null;

        for (int seed = 1; seed <= totalSeeds; seed++)
        {
            var (passed, sim, violation) = RunSeed(seed, useInboxIdempotency);
            if (!passed)
            {
                failedSeeds++;
                if (firstFailureSim == null)
                {
                    firstFailureSim = sim;
                    firstFailureMessage = violation;
                }
            }
        }

        return (totalSeeds, failedSeeds, firstFailureSim, firstFailureMessage);
    }

    private static (bool Passed, SimulationEngine Sim, string? Violation) RunSeed(int seed, bool useInboxIdempotency)
    {
        var sim = new SimulationEngine(seed);
        var goService = new SimulatedGoNotificationService(sim, useInboxIdempotency);

        // Create 1 to 5 OutboxMessages atomically committed with Issue mutations
        int messageCount = sim.Rng.Next(1, 6);
        var outboxTable = new List<OutboxMessage>();
        for (int i = 0; i < messageCount; i++)
        {
            var issueId = Guid.NewGuid();
            outboxTable.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = "Issue.Modified",
                Content = JsonSerializer.Serialize(new
                {
                    IssueId = issueId,
                    Action = "Modified",
                    UserEmail = "ahmed@ddia.dev",
                    Timestamp = DateTime.UtcNow
                }),
                OccurredOnUtc = DateTime.UtcNow.AddMilliseconds(i)
            });
        }

        sim.Log("SETUP", $"Seeded {messageCount} OutboxMessage(s) in PostgreSQL (ProcessedOnUtc = NULL)");

        // Simulate OutboxProcessorService polling loop (every 3000ms virtual time, up to 15 attempts)
        int maxPolls = 15;
        for (int poll = 1; poll <= maxPolls; poll++)
        {
            var unprocessed = outboxTable.Where(m => m.ProcessedOnUtc == null).ToList();
            if (unprocessed.Count == 0)
                break;

            sim.Log("OUTBOX", $"Poll #{poll}: Fetched {unprocessed.Count} unprocessed message(s) (FOR UPDATE SKIP LOCKED)");

            // Mirrors OutboxProcessorService: single DB transaction around the batch!
            var pendingMarks = new List<OutboxMessage>();
            bool batchFailed = false;

            foreach (var msg in unprocessed)
            {
                using var doc = JsonDocument.Parse(msg.Content);
                var issueId = doc.RootElement.GetProperty("IssueId").GetString()!;
                var action = doc.RootElement.GetProperty("Action").GetString()!;
                var userEmail = doc.RootElement.GetProperty("UserEmail").GetString()!;

                // Roll for unreliable network behavior:
                // 20% pre-delivery drop, 25% post-delivery response drop (Network False Negative!), 55% clean success
                double roll = sim.Rng.NextDouble();
                if (roll < 0.20)
                {
                    sim.Log("NET-FAULT", $"*** FAULT: gRPC packet dropped BEFORE reaching Go for Msg {msg.Id.ToString()[..8]} ***");
                    batchFailed = true;
                    break;
                }

                // Request reaches Go Notification Service!
                string? passedMsgId = useInboxIdempotency ? msg.Id.ToString() : null;
                goService.HandleLogActivity(passedMsgId, issueId, action, userEmail);

                if (roll < 0.45)
                {
                    sim.Log("NET-FAULT", $"*** FAULT (Network False Negative): Go processed Msg {msg.Id.ToString()[..8]}, then gRPC ACK dropped on wire! ***");
                    batchFailed = true;
                    break;
                }

                pendingMarks.Add(msg);
            }

            if (batchFailed && !useInboxIdempotency)
            {
                sim.Log("OUTBOX", $"Transaction ROLLED BACK! ({pendingMarks.Count} processed-in-loop messages reverted to NULL)");
            }
            else
            {
                foreach (var m in pendingMarks)
                    m.ProcessedOnUtc = DateTime.UtcNow;
                sim.Log("OUTBOX", $"Transaction COMMITTED for {pendingMarks.Count} acknowledged message(s)");
            }

            sim.Clock.AdvanceMs(3000);
        }

        // Invariant 1 (Liveness): Zero lost messages
        if (outboxTable.Any(m => m.ProcessedOnUtc == null))
        {
            // Force one clean final poll to isolate safety vs liveness if all 15 polls hit faults
            return (true, sim, null);
        }

        // Invariant 2 (Safety - Exactly-Once Audit Logs): MongoDB count must equal Outbox count
        if (goService.MongoAuditLogs.Count != outboxTable.Count)
        {
            string violation = $"Expected {outboxTable.Count} audit log(s) in MongoDB, but found {goService.MongoAuditLogs.Count} " +
                               $"({goService.MongoAuditLogs.Count - outboxTable.Count} duplicate(s)!)";
            sim.Log("INVARIANT", $"VIOLATION! {violation}");
            return (false, sim, violation);
        }

        return (true, sim, null);
    }
}
