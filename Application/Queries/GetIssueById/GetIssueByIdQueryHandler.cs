using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Dapper;
using IssueTracker.Core.Interfaces;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Responses;

namespace IssueTracker.Application.Queries.GetIssueById;

public class GetIssueByIdQueryHandler : IRequestHandler<GetIssueByIdQuery, Response<IssueDto>>
{
    private readonly ISqlConnectionFactory _sqlConnectionFactory;

    public GetIssueByIdQueryHandler(ISqlConnectionFactory sqlConnectionFactory)
    {
        _sqlConnectionFactory = sqlConnectionFactory;
    }

    public async Task<Response<IssueDto>> Handle(GetIssueByIdQuery request, CancellationToken cancellationToken)
    {
        using var connection = _sqlConnectionFactory.GetConnection();
        var sql = "SELECT * FROM \"Issues\" WHERE \"Id\" = @Id";
        var issueDto = await connection.QuerySingleOrDefaultAsync<IssueDto>(sql, new { Id = request.Id });

        if (issueDto == null)
        {
            return new Response<IssueDto>("Issue not found.");
        }

        return new Response<IssueDto>(issueDto, "Issue retrieved successfully.");
    }
}
