using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IssueTracker.Application.Commands.UpdateIssue;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Interfaces;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Enums;
using IssueTracker.Core.Interfaces;
using IssueTracker.Infrastructure.Services;
using StackExchange.Redis;

namespace IssueTracker.SimulationTests;

// ============================================================================
// 1. VIRTUAL CLOCK (Zero-Latency Time)
// Instead of real wall-clock time, time is just an integer we advance instantly.
// ============================================================================
public class VirtualClock
{
    public long CurrentTimeMs { get; private set; } = 0;

    public void AdvanceMs(long ms) => CurrentTimeMs += ms;
}

// ============================================================================
// 2. DETERMINISTIC TASK SCHEDULER & INTERCEPTOR
// Allows the simulator to pause a worker right in the middle of an async flow
// (e.g., right after reading Postgres, before writing to Redis!)
// ============================================================================
public class SimulationEngine
{
    public int Seed { get; }
    public Random Rng { get; }
    public VirtualClock Clock { get; } = new();
    public List<string> TraceLog { get; } = new();

    public SimulationEngine(int seed)
    {
        Seed = seed;
        Rng = new Random(seed);
    }

    public void Log(string actor, string message)
    {
        TraceLog.Add($"[t={Clock.CurrentTimeMs,5}ms] [{actor,-10}] {message}");
    }
}

// ============================================================================
// 3. SIMULATED REDIS (Implements StackExchange.Redis IDatabase & IConnectionMultiplexer)
// Evaluates key & lock TTL expiration against VirtualClock.CurrentTimeMs!
// ============================================================================
public class SimulatedRedis
{
    private readonly SimulationEngine _sim;
    private readonly Dictionary<string, (string Value, long? ExpiresAtMs)> _strings = new();
    private readonly Dictionary<string, Dictionary<string, double>> _zsets = new();

    public SimulatedRedis(SimulationEngine sim)
    {
        _sim = sim;
    }

    private void PurgeExpired(string key)
    {
        if (_strings.TryGetValue(key, out var entry) && entry.ExpiresAtMs.HasValue)
        {
            if (_sim.Clock.CurrentTimeMs >= entry.ExpiresAtMs.Value)
            {
                _strings.Remove(key);
                _sim.Log("REDIS", $"TTL Expired for key '{key}' at t={_sim.Clock.CurrentTimeMs}ms");
            }
        }
    }

    public bool StringSet(string key, string value, TimeSpan? expiry = null, When when = When.Always)
    {
        PurgeExpired(key);

        if (when == When.NotExists && _strings.ContainsKey(key))
            return false;

        long? expiresAt = expiry.HasValue ? _sim.Clock.CurrentTimeMs + (long)expiry.Value.TotalMilliseconds : null;
        _strings[key] = (value, expiresAt);
        return true;
    }

    public string? StringGet(string key)
    {
        PurgeExpired(key);
        return _strings.TryGetValue(key, out var entry) ? entry.Value : null;
    }

    public bool KeyExists(string key)
    {
        PurgeExpired(key);
        return _strings.ContainsKey(key);
    }

    public bool LockTake(string key, string token, TimeSpan expiry)
    {
        var acquired = StringSet(key, token, expiry, When.NotExists);
        _sim.Log("REDIS-LOCK", acquired
            ? $"Lock '{key}' ACQUIRED by token '{token[..6]}' (valid until t={_sim.Clock.CurrentTimeMs + (long)expiry.TotalMilliseconds}ms)"
            : $"Lock '{key}' BUSY (rejected token '{token[..6]}')");
        return acquired;
    }

    public bool LockRelease(string key, string token)
    {
        PurgeExpired(key);
        if (_strings.TryGetValue(key, out var entry) && entry.Value == token)
        {
            _strings.Remove(key);
            _sim.Log("REDIS-LOCK", $"Lock '{key}' RELEASED by token '{token[..6]}'");
            return true;
        }
        _sim.Log("REDIS-LOCK", $"Lock '{key}' RELEASE FAILED for token '{token[..6]}' (Lock already expired or owned by another node!)");
        return false;
    }

    public bool SortedSetAdd(string key, string member, double score)
    {
        if (!_zsets.TryGetValue(key, out var zset))
        {
            zset = new Dictionary<string, double>();
            _zsets[key] = zset;
        }
        bool added = !zset.ContainsKey(member);
        zset[member] = score;
        return added;
    }

    public string[] SortedSetRangeByScore(string key, double start, double stop, Exclude exclude, Order order, long skip, long take)
    {
        if (!_zsets.TryGetValue(key, out var zset) || zset.Count == 0)
            return Array.Empty<string>();

        var query = zset.AsEnumerable();
        double high = Math.Max(start, stop);
        double low = Math.Min(start, stop);

        query = query.Where(kv =>
        {
            if (exclude.HasFlag(Exclude.Start) && kv.Value == start) return false;
            if (exclude.HasFlag(Exclude.Stop) && kv.Value == stop) return false;
            return kv.Value <= high && kv.Value >= low;
        });

        query = order == Order.Descending
            ? query.OrderByDescending(kv => kv.Value)
            : query.OrderBy(kv => kv.Value);

        return query.Skip((int)skip).Take((int)take).Select(kv => kv.Key).ToArray();
    }

    public int EvalUpsertIssueScript(string issueKey, string versionKey, string zsetKey, int incomingVersion, string json, string issueId, double score)
    {
        var currentVersionStr = StringGet(versionKey);
        int currentVersion = currentVersionStr != null ? int.Parse(currentVersionStr) : 0;

        if (incomingVersion < currentVersion)
        {
            _sim.Log("REDIS-LUA", $"REJECTED zombie write for '{issueKey}': incoming Version {incomingVersion} < stored Version {currentVersion}");
            return 0;
        }

        StringSet(versionKey, incomingVersion.ToString());
        StringSet(issueKey, json);
        SortedSetAdd(zsetKey, issueId, score);
        _sim.Log("REDIS-LUA", $"COMMITTED '{issueKey}' at Version {incomingVersion}");
        return 1;
    }

    public void ClearCacheOnly()
    {
        _strings.Clear();
        _zsets.Clear();
    }
}
