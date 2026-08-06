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
            if (request.MinVersion == null || cachedIssue.Version >= request.MinVersion)
            {
                return new Response<IssueDto>(cachedIssue, "Issue retrieved from cache.");
            }
        }

        // 2. Fallback to PostgreSQL Read Replica (or Leader if Sticky Routing active)
        using var connection = request.useLeaderConnection 
            ? _sqlConnectionFactory.GetLeaderConnection() 
            : _sqlConnectionFactory.GetConnection();

        var sql = "SELECT * FROM \"Issues\" WHERE \"Id\" = @Id";
        var issueDto = await connection.QuerySingleOrDefaultAsync<IssueDto>(sql, new { Id = request.Id });

        if (issueDto == null)
        {
            return new Response<IssueDto>("Issue not found.");
        }

        // 3. Client-Centric Consistency (Logical Clock Validation)
        // If the Replica returned a stale version, instantly drop connection and query Leader
        if (request.MinVersion != null && issueDto.Version < request.MinVersion.Value && !request.useLeaderConnection)
        {
            using var leaderConnection = _sqlConnectionFactory.GetLeaderConnection();
            issueDto = await leaderConnection.QuerySingleOrDefaultAsync<IssueDto>(sql, new { Id = request.Id });

            if (issueDto == null)
            {
                return new Response<IssueDto>("Issue not found on Leader.");
            }
            
            return new Response<IssueDto>(issueDto, "Issue retrieved from Leader (Replica was lagging).");
        }

        return new Response<IssueDto>(issueDto, "Issue retrieved from database.");
    }
}
