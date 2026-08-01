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
        var cacheResult = await _cursorCacheService.GetIssueByCursorAsync(request.PageSize, request.CursorTimestamp, async () =>
        {
            using var connection = _sqlConnectionFactory.GetConnection();
            string sql;
            
            if (request.CursorTimestamp.HasValue)
            {
                var cursorDate = new DateTimeOffset(request.CursorTimestamp.Value, TimeSpan.Zero);
                sql = @"SELECT * FROM ""Issues"" WHERE ""CreatedAt"" < @CursorDate ORDER BY ""CreatedAt"" DESC LIMIT @PageSize";
                return await connection.QueryAsync<Issue>(sql, new { CursorDate = cursorDate, PageSize = request.PageSize });
            }
            else
            {
                sql = @"SELECT * FROM ""Issues"" ORDER BY ""CreatedAt"" DESC LIMIT @PageSize";
                return await connection.QueryAsync<Issue>(sql, new { PageSize = request.PageSize });
            }
        });

        return new CursorResponse<IEnumerable<IssueDto>>(cacheResult.Issues, cacheResult.NextCursor);
    }
}
