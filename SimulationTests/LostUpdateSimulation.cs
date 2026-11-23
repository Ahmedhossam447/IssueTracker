using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IssueTracker.Application.Commands.UpdateIssue;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Enums;
using IssueTracker.Infrastructure.Services;
using StackExchange.Redis;

namespace IssueTracker.SimulationTests;

// ============================================================================
// DDIA CHAPTER 8: CONCURRENT READ-MODIFY-WRITE (LOST UPDATE SIMULATION)
// Simulates two clients reading Version 1, then interleaving their updates
// concurrently. Without Optimistic Concurrency Control (First-Committer-Wins),
// the second writer silently overwrites the first writer's changes!
// ============================================================================
public static class LostUpdateSimulation
{
    public static (int TotalSeeds, int FailedSeeds, SimulationEngine? FirstFailureSim, string? FirstFailureMessage) Run(
        int totalSeeds)
    {
        int failedSeeds = 0;
        SimulationEngine? firstFailureSim = null;
        string? firstFailureMessage = null;

        for (int seed = 1; seed <= totalSeeds; seed++)
        {
            var (passed, sim, violation) = RunSeedAsync(seed).GetAwaiter().GetResult();
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

    private static async Task<(bool Passed, SimulationEngine Sim, string? Violation)> RunSeedAsync(int seed)
    {
        var sim = new SimulationEngine(seed);
        var simulatedRedis = new SimulatedRedis(sim);
        var simulatedPg = new SimulatedPostgres(sim);

        var dbProxy = DispatchProxy.Create<IDatabase, SimulatedRedisDatabaseProxy>();
        ((SimulatedRedisDatabaseProxy)(object)dbProxy).Redis = simulatedRedis;
        var muxProxy = DispatchProxy.Create<IConnectionMultiplexer, SimulatedConnectionMultiplexerProxy>();
        ((SimulatedConnectionMultiplexerProxy)(object)muxProxy).Database = dbProxy;

        var cacheService = new RedisCursorCacheService(muxProxy);
        var updateHandler = new UpdateIssueCommandHandler(
            simulatedPg,
            cacheService,
            new SimulatedCurrentUserService()
        );

        // 1. Seed Issue at Version 1
        var issue = simulatedPg.SeedIssue("Original Title", "Original Description", IssuePriority.Low);
        sim.Log("SETUP", $"Seeded Issue {issue.Id.ToString()[..8]} at Version {issue.Version} (Title='{issue.Title}', Priority={issue.Priority})");

        // 2. Client A and Client B both read Version 1 (e.g., open the same Issue edit form in their browsers)
        int clientAReadVersion = issue.Version;
        int clientBReadVersion = issue.Version;
        sim.Log("CLIENT-A", $"Opened Issue in browser (saw Version {clientAReadVersion})");
        sim.Log("CLIENT-B", $"Opened Issue in browser (saw Version {clientBReadVersion})");

        // 3. Deterministically decide whether Client A and Client B race concurrently
        bool concurrentRace = sim.Rng.NextDouble() < 0.60;
        bool clientASucceeded = false;
        bool clientBSucceeded = false;

        if (concurrentRace)
        {
            sim.Clock.AdvanceMs(sim.Rng.Next(10, 200));
            sim.Log("CLIENT-B", $"Submitting update based on Version {clientBReadVersion}: Title='Client B Title', Priority=Critical");
            try
            {
                var resB = await updateHandler.Handle(new UpdateIssueCommand(
                    issue.Id,
                    "Client B Title",
                    "Client B fixed description",
                    IssuePriority.Critical,
                    ExpectedVersion: clientBReadVersion
                ), CancellationToken.None);
                clientBSucceeded = resB.Succeeded;
            }
            catch (Exception ex)
            {
                sim.Log("CLIENT-B", $"Rejected with conflict: {ex.Message}");
            }

            sim.Clock.AdvanceMs(sim.Rng.Next(10, 200));
            sim.Log("CLIENT-A", $"Submitting stale update based on Version {clientAReadVersion}: Title='Client A Title', Priority=High");
            try
            {
                var resA = await updateHandler.Handle(new UpdateIssueCommand(
                    issue.Id,
                    "Client A Title",
                    "Client A fixed description",
                    IssuePriority.High,
                    ExpectedVersion: clientAReadVersion
                ), CancellationToken.None);
                clientASucceeded = resA.Succeeded;
            }
            catch (Exception ex)
            {
                sim.Log("CLIENT-A", $"Rejected with conflict: {ex.Message}");
            }
        }
        else
        {
            // Sequential non-overlapping edits (Client B updates, then Client A refreshes and sees Version 2)
            var resB = await updateHandler.Handle(new UpdateIssueCommand(
                issue.Id, "Client B Title", "Desc B", IssuePriority.Critical), CancellationToken.None);
            clientBSucceeded = resB.Succeeded;
            return (true, sim, null);
        }

        // 4. INVARIANT CHECK (No Lost Update):
        // When Client A and Client B both edited from Version 1 concurrently,
        // ONLY ONE may succeed. If both succeeded, Client B's update was silently lost!
        var finalState = simulatedPg.GetLeaderState(issue.Id);
        if (clientASucceeded && clientBSucceeded)
        {
            string violation = $"LOST UPDATE! Both Client B and stale Client A succeeded! " +
                               $"Final DB state is Version {finalState.Version} ('{finalState.Title}') — Client B's edit was silently overwritten!";
            sim.Log("INVARIANT", $"VIOLATION! {violation}");
            return (false, sim, violation);
        }

        return (true, sim, null);
    }
}
