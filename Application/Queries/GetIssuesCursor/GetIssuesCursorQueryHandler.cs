using MediatR;
using Dapper;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Interfaces;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Interfaces;
using IssueTracker.Application.Responses;

namespace IssueTracker.Application.Queries.GetIssuesCursor;

public class GetIssuesCursorQueryHandler : IRequestHandler<GetIssuesCursorQuery, CursorResponse<IEnumerable<IssueDto>>>
{
    private readonly ISqlConnectionFactory _sqlConnectionFactory;
    private readonly ICursorCacheService _cursorCacheService;

    public GetIssuesCursorQueryHandler(ISqlConnectionFactory sqlConnectionFactory, ICursorCacheService cursorCacheService)
    {
        _sqlConnectionFactory = sqlConnectionFactory;
        _cursorCacheService = cursorCacheService;
    }

    public async Task<CursorResponse<IEnumerable<IssueDto>>> Handle(GetIssuesCursorQuery request, CancellationToken cancellationToken)
    {
        var cacheResult = await _cursorCacheService.GetIssueByCursorAsync(request.PageSize, request.CursorTimestamp);
        
        if (cacheResult.Issues.Any())
        {
            return new CursorResponse<IEnumerable<IssueDto>>(cacheResult.Issues, cacheResult.NextCursor);
        }

        // 2. CACHE MISS! The user scrolled into cold data, or the cache was just cleared.
        // We must query PostgreSQL using Dapper to fetch the data.
        using var connection = _sqlConnectionFactory.GetConnection();

        string sql;
        IEnumerable<Issue> dbIssues;

        if (request.CursorTimestamp.HasValue)
        {
            // Convert the Ticks back into a DateTimeOffset so PostgreSQL can use its B-Tree Index!
            var cursorDate = new DateTimeOffset(request.CursorTimestamp.Value, TimeSpan.Zero);
            
            sql = @"SELECT * FROM ""Issues"" WHERE ""CreatedAt"" < @CursorDate ORDER BY ""CreatedAt"" DESC LIMIT @PageSize";
            dbIssues = await connection.QueryAsync<Issue>(sql, new { CursorDate = cursorDate, PageSize = request.PageSize });
        }
        else
        {
            // First page, no cursor provided
            sql = @"SELECT * FROM ""Issues"" ORDER BY ""CreatedAt"" DESC LIMIT @PageSize";
            dbIssues = await connection.QueryAsync<Issue>(sql, new { PageSize = request.PageSize });
        }

        // 3. WARM THE CACHE! Push the missing data we just found back into Redis concurrently.
        var cacheTasks = dbIssues.Select(issue => _cursorCacheService.AddOrUpdateIssueAsync(issue));
        await Task.WhenAll(cacheTasks);

        var dtos = dbIssues.Select(issue => new IssueDto
        {
            Id = issue.Id,
            Title = issue.Title,
            Description = issue.Description,
            Status = issue.Status,
            Priority = issue.Priority,
            CreatedAt = issue.CreatedAt,
            UpdatedAt = issue.UpdatedAt
        }).ToList();

        long? nextCursor = dtos.LastOrDefault()?.CreatedAt.Ticks;

        return new CursorResponse<IEnumerable<IssueDto>>(dtos, nextCursor);
    }
}
