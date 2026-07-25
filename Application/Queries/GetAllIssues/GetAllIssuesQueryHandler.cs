using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Dapper;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Interfaces;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Responses;

namespace IssueTracker.Application.Queries.GetAllIssues;

public class GetAllIssuesQueryHandler : IRequestHandler<GetAllIssuesQuery, PagedResponse<IEnumerable<IssueDto>>>
{
    private readonly ISqlConnectionFactory _sqlConnectionFactory;

    public GetAllIssuesQueryHandler(ISqlConnectionFactory sqlConnectionFactory)
    {
        _sqlConnectionFactory = sqlConnectionFactory;
    }

    public async Task<PagedResponse<IEnumerable<IssueDto>>> Handle(GetAllIssuesQuery request, CancellationToken cancellationToken)
    {
        var sql = @"
    SELECT COUNT(*) as TotalCount FROM ""Issues"";
    SELECT * FROM ""Issues"" LIMIT @PageSize OFFSET @Offset";
        int offset = (request.PageNumber - 1) * request.PageSize;
        using var connection = _sqlConnectionFactory.GetConnection();
        var result = await connection.QueryMultipleAsync(sql, new { PageSize = request.PageSize, Offset = offset });
        var totalCount =await result.ReadFirstAsync<int>();
        var issues = await result.ReadAsync<IssueDto>();
        return new PagedResponse<IEnumerable<IssueDto>>(
            issues, 
            request.PageNumber, 
            request.PageSize, 
            totalCount, 
            "Issues retrieved successfully.");
    }
}
