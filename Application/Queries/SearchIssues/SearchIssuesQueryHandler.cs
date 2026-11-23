using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MediatR;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Responses;
using IssueTracker.Core.Interfaces;

namespace IssueTracker.Application.Queries.SearchIssues;

/// <summary>
/// DDIA Ch. 4: Executes Full-Text Search against the PostgreSQL GIN Inverted Index
/// via Dapper on Read Replicas (Port 5433), ranking Title ('A') above Description ('B').
/// </summary>
public class SearchIssuesQueryHandler : IRequestHandler<SearchIssuesQuery, PagedResponse<IEnumerable<IssueDto>>>
{
    private readonly ISqlConnectionFactory _sqlConnectionFactory;

    public SearchIssuesQueryHandler(ISqlConnectionFactory sqlConnectionFactory)
    {
        _sqlConnectionFactory = sqlConnectionFactory;
    }

    public async Task<PagedResponse<IEnumerable<IssueDto>>> Handle(SearchIssuesQuery request, CancellationToken cancellationToken)
    {
        const string sql = @"
            WITH q AS (SELECT websearch_to_tsquery('english', @SearchTerm) AS query)
            SELECT COUNT(*) AS TotalCount
            FROM ""Issues"", q
            WHERE ""SearchVector"" @@ q.query;

            WITH q AS (SELECT websearch_to_tsquery('english', @SearchTerm) AS query)
            SELECT ""Id"", ""Title"", ""Description"", ""Status"", ""Priority"", ""CreatedAt"", ""UpdatedAt"", ""Version""
            FROM ""Issues"", q
            WHERE ""SearchVector"" @@ q.query
            ORDER BY ts_rank(""SearchVector"", q.query) DESC, ""CreatedAt"" DESC
            LIMIT @PageSize OFFSET @Offset;";

        int pageNumber = request.PageNumber < 1 ? 1 : request.PageNumber;
        int pageSize = request.PageSize < 1 ? 10 : request.PageSize;
        int offset = (pageNumber - 1) * pageSize;

        using var connection = _sqlConnectionFactory.GetConnection();
        using var result = await connection.QueryMultipleAsync(sql, new
        {
            SearchTerm = request.Query,
            PageSize = pageSize,
            Offset = offset
        });

        var totalCount = await result.ReadFirstAsync<int>();
        var issues = await result.ReadAsync<IssueDto>();

        return new PagedResponse<IEnumerable<IssueDto>>(
            issues,
            pageNumber,
            pageSize,
            totalCount,
            "Issues searched successfully using GIN inverted index.");
    }
}
