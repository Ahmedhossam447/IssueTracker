using System;
using MediatR;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Responses;

namespace IssueTracker.Application.Queries.GetIssueById;

public record GetIssueByIdQuery(Guid Id) : IRequest<Response<IssueDto>>;
