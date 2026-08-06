using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Dapper;
using IssueTracker.Core.Interfaces;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Responses;
using IssueTracker.Application.Interfaces;

namespace IssueTracker.Application.Queries.GetIssueById;

public class GetIssueByIdQueryHandler : IRequestHandler<GetIssueByIdQuery, Response<IssueDto>>
{
    private readonly ISqlConnectionFactory _sqlConnectionFactory;
    private readonly ICursorCacheService _cacheService;

    public GetIssueByIdQueryHandler(ISqlConnectionFactory sqlConnectionFactory, ICursorCacheService cacheService)
    {
        _sqlConnectionFactory = sqlConnectionFactory;
        _cacheService = cacheService;
    }

    public async Task<Response<IssueDto>> Handle(GetIssueByIdQuery request, CancellationToken cancellationToken)
    {
        // 1. Try to fetch from Redis Cache to prevent Read-Your-Own-Writes lag
        var cachedIssue = await _cacheService.GetIssueByIdAsync(request.Id);
        if (cachedIssue != null)
        {
            return new Response<IssueDto>(cachedIssue, "Issue retrieved from cache.");
        }

        using var connection = request.useLeaderConnection
             ? _sqlConnectionFactory.GetLeaderConnection()
             : _sqlConnectionFactory.GetConnection();


        var sql = "SELECT * FROM \"Issues\" WHERE \"Id\" = @Id";
        var issueDto = await connection.QuerySingleOrDefaultAsync<IssueDto>(sql, new { Id = request.Id });

        if (issueDto == null)
        {
            return new Response<IssueDto>("Issue not found.");
        }

        return new Response<IssueDto>(issueDto, "Issue retrieved from database.");
    }
}
