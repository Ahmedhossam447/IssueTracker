using MediatR;
using IssueTracker.Core.Enums;

using IssueTracker.Application.Responses;

namespace IssueTracker.Application.Commands.CreateIssue;

public record CreateIssueCommand(string Title, string Description, IssuePriority Priority) : IRequest<Response<Guid>>;
