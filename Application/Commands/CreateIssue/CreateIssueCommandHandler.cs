using MediatR;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Interfaces;

using IssueTracker.Application.Responses;

namespace IssueTracker.Application.Commands.CreateIssue;

public class CreateIssueCommandHandler : IRequestHandler<CreateIssueCommand, Response<Guid>>
{
    private readonly IGenericRepository<Issue> _repository;

    public CreateIssueCommandHandler(IGenericRepository<Issue> repository)
    {
        _repository = repository;
    }

    public async Task<Response<Guid>> Handle(CreateIssueCommand request, CancellationToken cancellationToken)
    {
        var issue = Issue.Create(request.Title, request.Description, request.Priority);
        
        await _repository.AddAsync(issue);
        
        return new Response<Guid>(issue.Id, "Issue created successfully.");
    }
}
