using MediatR;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Responses;

namespace IssueTracker.Application.Queries.GetIssuesCursor;

public record GetIssuesCursorQuery(long? CursorTimestamp, int PageSize) : IRequest<CursorResponse<IEnumerable<IssueDto>>>;
