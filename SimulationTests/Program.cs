using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IssueTracker.Application.Commands.UpdateIssue;
using IssueTracker.Application.DTOs;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Enums;
using IssueTracker.Infrastructure.Services;
using StackExchange.Redis;

namespace IssueTracker.SimulationTests;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("  DDIA CHAPTER 9: DETERMINISTIC SIMULATION TESTING (DST) HARNESS");
        Console.WriteLine("  Target: RedisCursorCacheService (Distributed Lock & Zombie Pause Invariant)");
        Console.WriteLine("================================================================================\n");

        var sw = Stopwatch.StartNew();
        int totalSeeds = 500;
        int failedSeeds = 0;
        SimulationEngine? firstFailureSim = null;
        string? firstFailureMessage = null;

        for (int seed = 1; seed <= totalSeeds; seed++)
        {
            var (passed, sim, violation) = await RunSimulationForSeedAsync(seed);
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
        sw.Stop();

        Console.WriteLine($"Executed {totalSeeds} randomized simulation universes in {sw.ElapsedMilliseconds} ms (Virtual Time).\n");

        if (firstFailureSim != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[INVARIANT VIOLATION DETECTED!] {failedSeeds} / {totalSeeds} seeds failed.");
            Console.WriteLine($"Replaying exact deterministic failure from Seed #{firstFailureSim.Seed}:");
            Console.WriteLine($"Reason: {firstFailureMessage}\n");
            Console.ResetColor();

            Console.WriteLine("--- Deterministic Execution Trace (Seed #" + firstFailureSim.Seed + ") ---");
            foreach (var line in firstFailureSim.TraceLog)
            {
                if (line.Contains("FAULT INJECTED") || line.Contains("RELEASE FAILED") || line.Contains("VIOLATION"))
                    Console.ForegroundColor = ConsoleColor.Yellow;
                else if (line.Contains("PG-LEADER"))
                    Console.ForegroundColor = ConsoleColor.Cyan;
                else if (line.Contains("REDIS-LOCK"))
                    Console.ForegroundColor = ConsoleColor.Green;
                else
                    Console.ResetColor();

                Console.WriteLine("  " + line);
            }
            Console.ResetColor();
            Console.WriteLine("--------------------------------------------------------------------------------");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[ALL {totalSeeds} SEEDS PASSED!] Zero invariant violations detected across all simulated faults.");
            Console.ResetColor();
        }

        Console.WriteLine("\n================================================================================");
        Console.WriteLine("  TEST #2: OUTBOX -> gRPC -> NOTIFICATION SERVICE (NETWORK DROP & RETRIES)");
        Console.WriteLine("================================================================================\n");

        var sw2 = Stopwatch.StartNew();
        var (outboxTotal, outboxFailed, outboxFirstSim, outboxFirstMsg) = OutboxNetworkSimulation.Run(
            totalSeeds: 500,
            useInboxIdempotency: true);
        sw2.Stop();

        Console.WriteLine($"Executed {outboxTotal} randomized network-drop universes in {sw2.ElapsedMilliseconds} ms (Virtual Time).\n");

        if (outboxFirstSim != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[INVARIANT VIOLATION DETECTED!] {outboxFailed} / {outboxTotal} seeds failed.");
            Console.WriteLine($"Replaying exact deterministic failure from Seed #{outboxFirstSim.Seed}:");
            Console.WriteLine($"Reason: {outboxFirstMsg}\n");
            Console.ResetColor();

            Console.WriteLine("--- Deterministic Execution Trace (Seed #" + outboxFirstSim.Seed + ") ---");
            foreach (var line in outboxFirstSim.TraceLog)
            {
                if (line.Contains("FAULT") || line.Contains("VIOLATION") || line.Contains("ROLLED BACK"))
                    Console.ForegroundColor = ConsoleColor.Yellow;
                else if (line.Contains("GO-MONGO") || line.Contains("GO-INBOX"))
                    Console.ForegroundColor = ConsoleColor.Cyan;
                else
                    Console.ResetColor();
                Console.WriteLine("  " + line);
            }
            Console.ResetColor();
            Console.WriteLine("--------------------------------------------------------------------------------");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[ALL {outboxTotal} SEEDS PASSED!] Zero duplicate audit logs detected.");
            Console.ResetColor();
        }

        Console.WriteLine("\n================================================================================");
        Console.WriteLine("  TEST #3: CONCURRENT UPDATEISSUE EDITS (LOST UPDATE INVARIANT)");
        Console.WriteLine("================================================================================\n");

        var sw3 = Stopwatch.StartNew();
        var (luTotal, luFailed, luFirstSim, luFirstMsg) = LostUpdateSimulation.Run(totalSeeds: 500);
        sw3.Stop();

        Console.WriteLine($"Executed {luTotal} randomized concurrent-edit universes in {sw3.ElapsedMilliseconds} ms (Virtual Time).\n");

        if (luFirstSim != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[INVARIANT VIOLATION DETECTED!] {luFailed} / {luTotal} seeds failed.");
            Console.WriteLine($"Replaying exact deterministic failure from Seed #{luFirstSim.Seed}:");
            Console.WriteLine($"Reason: {luFirstMsg}\n");
            Console.ResetColor();

            Console.WriteLine("--- Deterministic Execution Trace (Seed #" + luFirstSim.Seed + ") ---");
            foreach (var line in luFirstSim.TraceLog)
            {
                if (line.Contains("VIOLATION"))
                    Console.ForegroundColor = ConsoleColor.Yellow;
                else if (line.Contains("PG-LEADER"))
                    Console.ForegroundColor = ConsoleColor.Cyan;
                else
                    Console.ResetColor();
                Console.WriteLine("  " + line);
            }
            Console.ResetColor();
            Console.WriteLine("--------------------------------------------------------------------------------");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[ALL {luTotal} SEEDS PASSED!] Zero lost updates detected.");
            Console.ResetColor();
        }
    }

    private static async Task<(bool Passed, SimulationEngine Sim, string? Violation)> RunSimulationForSeedAsync(int seed)
    {
        var sim = new SimulationEngine(seed);
        var simulatedRedis = new SimulatedRedis(sim);
        var simulatedPg = new SimulatedPostgres(sim);

        // Wire up your real production RedisCursorCacheService and UpdateIssueCommandHandler!
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

        // 1. Seed initial state: Issue exists in Postgres at Version 1, but Redis cache is cold (empty)
        var initialIssue = simulatedPg.SeedIssue("Initial Bug Report", "Prod DB is slow", IssuePriority.High);
        sim.Log("SETUP", $"Seeded Issue {initialIssue.Id.ToString()[..8]} in Postgres at Version {initialIssue.Version} (Redis cache is cold)");

        // 2. Worker-1 gets a Cache Miss on GetIssueByCursorAsync, acquires the 5000ms Redis Lock,
        //    and queries PostgreSQL inside dbFallbackQuery.
        sim.Log("WORKER-1", "Calling GetIssueByCursorAsync (Cache Miss -> Acquiring 5000ms Lock -> Querying Postgres)");

        await cacheService.GetIssueByCursorAsync(
            PageSize: 10,
            TimeStamp: null,
            dbFallbackQuery: async () =>
            {
                // Worker-1 reads Version 1 from Postgres Replica/Leader
                var rows = (await simulatedPg.GetAllAsync()).ToList();
                sim.Log("WORKER-1", $"Read Issue from Postgres at Version {rows[0].Version}");

                // DETERMINISTIC FAULT INJECTION (Seed-driven):
                // Decide whether a GC pause / VM freeze hits Worker-1 after reading DB, before writing to Redis!
                bool injectGcPause = sim.Rng.NextDouble() < 0.35; // 35% chance of pause
                long pauseDurationMs = sim.Rng.Next(1000, 8000);  // Pause between 1s and 8s (Lock TTL is 5000ms!)

                if (injectGcPause)
                {
                    sim.Log("FAULT", $"*** FAULT INJECTED: Worker-1 frozen in GC pause for {pauseDurationMs}ms ***");
                    sim.Clock.AdvanceMs(pauseDurationMs);

                    // While Worker-1 is frozen, does Client-2 update the Issue?
                    bool concurrentUpdateDuringPause = sim.Rng.NextDouble() < 0.75;
                    if (concurrentUpdateDuringPause)
                    {
                        sim.Log("CLIENT-2", "Sending UpdateIssueCommand while Worker-1 is paused...");
                        await updateHandler.Handle(new UpdateIssueCommand(
                            initialIssue.Id,
                            "Updated Bug Report (Fixed!)",
                            "Added index",
                            IssuePriority.Critical
                        ), CancellationToken.None);

                        var cachedAfterUpdate = await cacheService.GetIssueByIdAsync(initialIssue.Id);
                        sim.Log("CLIENT-2", $"Update complete! Redis cache now holds Version {cachedAfterUpdate?.Version}");
                    }

                    sim.Log("WORKER-1", $"Woke up from {pauseDurationMs}ms GC pause! Continuing execution to populate Redis cache...");
                }

                return rows;
            });

        // 3. INVARIANT CHECK (Safety Property: Linearizable Cache Freshness / No Zombie Overwrite)
        // The version in Redis Cache must NEVER be older than the version committed in Postgres!
        var leaderIssue = simulatedPg.GetLeaderState(initialIssue.Id);
        var cachedIssue = await cacheService.GetIssueByIdAsync(initialIssue.Id);

        if (cachedIssue != null && cachedIssue.Version < leaderIssue.Version)
        {
            string violation = $"Postgres Leader has Version {leaderIssue.Version} ('{leaderIssue.Title}'), " +
                               $"but Worker-1 overwrote Redis Cache back to stale Version {cachedIssue.Version} ('{cachedIssue.Title}')!";
            sim.Log("INVARIANT", $"VIOLATION! {violation}");
            return (false, sim, violation);
        }

        return (true, sim, null);
    }
}
