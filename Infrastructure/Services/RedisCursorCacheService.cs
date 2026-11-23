using IssueTracker.Application.DTOs;
using IssueTracker.Application.Interfaces;
using IssueTracker.Core.Entities;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace IssueTracker.Infrastructure.Services
{
    public class RedisCursorCacheService : ICursorCacheService
    {
        IConnectionMultiplexer _connectionMultiplexer;

        // Atomic Fencing-Token gatekeeper executed inside Redis's single-threaded Lua runtime.
        // Rejects any stale/zombie write whose Version is lower than what Redis already holds.
        private const string UpsertIssueLuaScript = @"
            local currentVersion = tonumber(redis.call('GET', KEYS[2]) or '0')
            if tonumber(ARGV[1]) < currentVersion then
                return 0
            end
            redis.call('SET', KEYS[2], ARGV[1])
            redis.call('SET', KEYS[1], ARGV[2])
            redis.call('ZADD', KEYS[3], ARGV[4], ARGV[3])
            return 1";
        
        public RedisCursorCacheService(IConnectionMultiplexer connectionMultiplexer)
        {
            _connectionMultiplexer = connectionMultiplexer;
        }
        public async Task<bool> AddOrUpdateIssueAsync(Issue issue)
        {
            try
            {
                IssueDto issueDto = new IssueDto
                {
                    Id = issue.Id,
                    Title = issue.Title,
                    Description = issue.Description,
                    Status = issue.Status,
                    Priority = issue.Priority,
                    CreatedAt = issue.CreatedAt,
                    UpdatedAt = issue.UpdatedAt,
                    Version = issue.Version
                };
                var json = JsonSerializer.Serialize(issueDto);

                var db = _connectionMultiplexer.GetDatabase();
                var result = (int)await db.ScriptEvaluateAsync(
                    UpsertIssueLuaScript,
                    new RedisKey[] { $"Issue:{issue.Id}", $"Issue:{issue.Id}:Version", "Issues" },
                    new RedisValue[] { issue.Version, json, issue.Id.ToString(), issue.CreatedAt.Ticks }
                );

                return result == 1;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // Soft SPOF: If Redis is down or frozen (SIGSTOP), ignore the cache update and keep the app alive
                return false;
            }
        }

        public async Task<IssueDto?> GetIssueByIdAsync(Guid issueId)
        {
            try
            {
                var db = _connectionMultiplexer.GetDatabase();
                var cachedJson = await db.StringGetAsync($"Issue:{issueId}");
                
                if (cachedJson.HasValue)
                {
                    return JsonSerializer.Deserialize<IssueDto>(cachedJson.ToString());
                }
                return null;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // Soft SPOF
                return null;
            }
        }

        public async Task<(IEnumerable<IssueDto> Issues, long? NextCursor)> GetIssueByCursorAsync(int PageSize, long? TimeStamp, Func<Task<IEnumerable<Issue>>> dbFallbackQuery)
        {
            try
            {
                var startScore = TimeStamp ?? double.PositiveInfinity;
                var db = _connectionMultiplexer.GetDatabase();
                var result = await db.SortedSetRangeByScoreAsync(
                    "Issues",
                    start: startScore,
                    stop: double.NegativeInfinity,
                    exclude: Exclude.Start,
                    order: Order.Descending,
                    skip: 0,
                    take: PageSize
                );

                if (result.Length == 0)
                {
                    // Cache Penetration Protection: Check if we already know this cursor is empty
                    var emptyCacheKey = $"Issues:Empty:{TimeStamp ?? 0}";
                    var isEmpty = await db.KeyExistsAsync(emptyCacheKey);
                    if (isEmpty)
                    {
                        return (new List<IssueDto>(), null);
                    }

                    var lockKey = $"Issues:Lock:{TimeStamp ?? 0}";
                    var lockToken = Guid.NewGuid().ToString();
                    
                    // Try to acquire the lock for 5 seconds
                    var lockAcquired = await db.LockTakeAsync(lockKey, lockToken, TimeSpan.FromSeconds(5));
                    if (lockAcquired)
                    {
                        try
                        {
                            var issuesFromDb = await dbFallbackQuery();
                            
                            var dtos = new List<IssueDto>();
                            foreach (var issue in issuesFromDb)
                            {
                                await AddOrUpdateIssueAsync(issue);
                                dtos.Add(new IssueDto
                                {
                                    Id = issue.Id,
                                    Title = issue.Title,
                                    Description = issue.Description,
                                    Status = issue.Status,
                                    Priority = issue.Priority,
                                    CreatedAt = issue.CreatedAt,
                                    UpdatedAt = issue.UpdatedAt,
                                    Version = issue.Version
                                });
                            }
                            
                            // Negative Caching: If the database is empty, cache this fact for 30 seconds
                            if (!dtos.Any())
                            {
                                await db.StringSetAsync(emptyCacheKey, "empty", TimeSpan.FromSeconds(30));
                                return (dtos, null);
                            }

                            long? dbNextCursor = dtos.LastOrDefault()?.CreatedAt.Ticks;
                            return (dtos, dbNextCursor);
                        }
                        finally
                        {
                            await db.LockReleaseAsync(lockKey, lockToken);
                        }
                    }
                    else
                    {
                        // Thundering herd! Another thread is fetching the data.
                        // Wait 50ms and try reading from Redis again.
                        await Task.Delay(50);
                        return await GetIssueByCursorAsync(PageSize, TimeStamp, dbFallbackQuery);
                    }
                }

                var redisKeys = new List<RedisKey>();
                redisKeys = result.Select(id => (RedisKey)$"Issue:{id}").ToList();
                var jsonResults = await db.StringGetAsync(redisKeys.ToArray());

                var issues = jsonResults
                     .Where(json => json.HasValue)
                     .Select(json => JsonSerializer.Deserialize<IssueDto>(json.ToString()))
                     .ToList();

                long? nextCursor = issues.LastOrDefault()?.CreatedAt.Ticks;

                return (issues, nextCursor);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // Soft SPOF: Redis is completely down or frozen! 
                // Don't crash the API, gracefully fall back to PostgreSQL directly
                var issuesFromDb = await dbFallbackQuery();
                
                var dtos = issuesFromDb.Select(issue => new IssueDto
                {
                    Id = issue.Id,
                    Title = issue.Title,
                    Description = issue.Description,
                    Status = issue.Status,
                    Priority = issue.Priority,
                    CreatedAt = issue.CreatedAt,
                    UpdatedAt = issue.UpdatedAt,
                    Version = issue.Version
                }).ToList();

                long? dbNextCursor = dtos.LastOrDefault()?.CreatedAt.Ticks;
                return (dtos, dbNextCursor);
            }
        }

        public async Task<bool> SetUserLatestVersionAsync(string userEmail, int version)
        {
            try
            {
                var db = _connectionMultiplexer.GetDatabase();
                await db.StringSetAsync($"User:{userEmail}:LatestVersion", version, TimeSpan.FromSeconds(20));
                return true;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // Soft SPOF: If Redis is down or frozen, gracefully degrade without crashing
                return false;
            }
        }

        public async Task<int?> GetUserLatestVersionAsync(string userEmail)
        {
            try
            {
                var db = _connectionMultiplexer.GetDatabase();
                var val = await db.StringGetAsync($"User:{userEmail}:LatestVersion");
                if (val.HasValue && int.TryParse(val.ToString(), out var version))
                {
                    return version;
                }
                return null;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // Soft SPOF
                return null;
            }
        }
    }
}
