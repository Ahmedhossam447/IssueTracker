using System;
using MediatR;
using IssueTracker.Application.Responses;
using IssueTracker.Core.Enums;

namespace IssueTracker.Application.Commands.UpdateIssue;

public record UpdateIssueCommand(Guid Id, string Title, string Description, IssuePriority Priority) : IRequest<Response<Guid>>;
