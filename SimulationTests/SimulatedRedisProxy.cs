using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IssueTracker.Application.Commands.UpdateIssue;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Interfaces;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Enums;
using IssueTracker.Infrastructure.Services;
using StackExchange.Redis;

namespace IssueTracker.SimulationTests;

// ============================================================================
// DYNAMIC PROXY FOR StackExchange.Redis (IDatabase & IConnectionMultiplexer)
// Connects your real production RedisCursorCacheService directly to SimulatedRedis!
// ============================================================================
public class SimulatedRedisDatabaseProxy : DispatchProxy
{
    public SimulatedRedis Redis { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null || args == null) return null;

        switch (targetMethod.Name)
        {
            case nameof(IDatabase.StringSetAsync):
            {
                string key = ((RedisKey)args[0]!).ToString();
                string val = ((RedisValue)args[1]!).ToString();
                TimeSpan? expiry = args.Length > 2 && args[2] is TimeSpan ts ? ts : null;
                When when = args.Length > 4 && args[4] is When w ? w : (args.Length > 3 && args[3] is When w2 ? w2 : When.Always);
                return Task.FromResult(Redis.StringSet(key, val, expiry, when));
            }
            case nameof(IDatabase.StringGetAsync):
            {
                if (args[0] is RedisKey singleKey)
                {
                    var val = Redis.StringGet(singleKey.ToString());
                    RedisValue rv = val != null ? (RedisValue)val : RedisValue.Null;
                    return Task.FromResult(rv);
                }
                if (args[0] is RedisKey[] keys)
                {
                    var results = keys.Select(k =>
                    {
                        var val = Redis.StringGet(k.ToString());
                        return val != null ? (RedisValue)val : RedisValue.Null;
                    }).ToArray();
                    return Task.FromResult(results);
                }
                break;
            }
            case nameof(IDatabase.KeyExistsAsync):
            {
                string key = ((RedisKey)args[0]!).ToString();
                return Task.FromResult(Redis.KeyExists(key));
            }
            case nameof(IDatabase.LockTakeAsync):
            {
                string key = ((RedisKey)args[0]!).ToString();
                string token = ((RedisValue)args[1]!).ToString();
                TimeSpan expiry = (TimeSpan)args[2]!;
                return Task.FromResult(Redis.LockTake(key, token, expiry));
            }
            case nameof(IDatabase.LockReleaseAsync):
            {
                string key = ((RedisKey)args[0]!).ToString();
                string token = ((RedisValue)args[1]!).ToString();
                return Task.FromResult(Redis.LockRelease(key, token));
            }
            case nameof(IDatabase.SortedSetAddAsync):
            {
                string key = ((RedisKey)args[0]!).ToString();
                string member = ((RedisValue)args[1]!).ToString();
                double score = (double)args[2]!;
                return Task.FromResult(Redis.SortedSetAdd(key, member, score));
            }
            case nameof(IDatabase.SortedSetRangeByScoreAsync):
            {
                string key = ((RedisKey)args[0]!).ToString();
                double start = (double)args[1]!;
                double stop = (double)args[2]!;
                Exclude exclude = (Exclude)args[3]!;
                Order order = (Order)args[4]!;
                long skip = (long)args[5]!;
                long take = (long)args[6]!;
                var members = Redis.SortedSetRangeByScore(key, start, stop, exclude, order, skip, take)
                    .Select(m => (RedisValue)m)
                    .ToArray();
                return Task.FromResult(members);
            }
            case nameof(IDatabase.ScriptEvaluateAsync):
            {
                var keys = (RedisKey[])args[1]!;
                var values = (RedisValue[])args[2]!;
                int result = Redis.EvalUpsertIssueScript(
                    issueKey: keys[0].ToString(),
                    versionKey: keys[1].ToString(),
                    zsetKey: keys[2].ToString(),
                    incomingVersion: (int)values[0],
                    json: values[1].ToString(),
                    issueId: values[2].ToString(),
                    score: (double)values[3]
                );
                return Task.FromResult(RedisResult.Create((RedisValue)result));
            }
        }
        throw new NotImplementedException($"SimulatedRedis does not implement {targetMethod.Name}");
    }
}

public class SimulatedConnectionMultiplexerProxy : DispatchProxy
{
    public IDatabase Database { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IConnectionMultiplexer.GetDatabase))
            return Database;
        throw new NotImplementedException($"Multiplexer method {targetMethod?.Name} not implemented");
    }
}

public class SimulatedCurrentUserService : ICurrentUserService
{
    public string UserEmail => "ahmed@ddia.dev";
}
