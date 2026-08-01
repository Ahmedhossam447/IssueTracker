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
        
        public RedisCursorCacheService(IConnectionMultiplexer connectionMultiplexer)
        {
            _connectionMultiplexer = connectionMultiplexer;
        }
        public async Task<bool> AddOrUpdateIssueAsync(Issue issue)
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
            };
            var json = JsonSerializer.Serialize(issueDto);

            var db = _connectionMultiplexer.GetDatabase();
            await db.StringSetAsync($"Issue:{issue.Id}", json);
            await db.SortedSetAddAsync("Issues", issue.Id.ToString(), issue.CreatedAt.Ticks);

            return true;
        }

        public async Task<(IEnumerable<IssueDto> Issues, long? NextCursor)> GetIssueByCursorAsync(int PageSize, long? TimeStamp)
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

            var redisKeys = new List<RedisKey>();
            redisKeys = result.Select(id => (RedisKey)$"Issue:{id}").ToList();
            var jsonResults = await db.StringGetAsync(redisKeys.ToArray());

            var issues = jsonResults
                 .Select(json => JsonSerializer.Deserialize<IssueDto>(json.ToString()))
                 .ToList();

            long? nextCursor = issues.LastOrDefault()?.CreatedAt.Ticks;

            return (issues, nextCursor);
        }
    }
}
