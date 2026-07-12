using System;
using MediatR;
using IssueTracker.Application.Responses;

namespace IssueTracker.Application.Commands.DeleteIssue;

public record DeleteIssueCommand(Guid Id) : IRequest<Response<Guid>>;
