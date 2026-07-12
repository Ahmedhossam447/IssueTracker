using System.Collections.Generic;
using MediatR;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Responses;

namespace IssueTracker.Application.Queries.GetAllIssues;

public record GetAllIssuesQuery(int PageNumber = 1, int PageSize = 10) : IRequest<PagedResponse<IEnumerable<IssueDto>>>;
