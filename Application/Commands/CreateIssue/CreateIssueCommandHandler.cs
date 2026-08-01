using MediatR;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Interfaces;

using IssueTracker.Application.Responses;
using IssueTracker.Application.Interfaces;

namespace IssueTracker.Application.Commands.CreateIssue;

public class CreateIssueCommandHandler : IRequestHandler<CreateIssueCommand, Response<Guid>>
{
    private readonly IGenericRepository<Issue> _repository;
    private readonly ICursorCacheService _cursorCacheService;
    public CreateIssueCommandHandler(IGenericRepository<Issue> repository, ICursorCacheService cursorCacheService)
    {
        _repository = repository;
        _cursorCacheService = cursorCacheService;
    }

    public async Task<Response<Guid>> Handle(CreateIssueCommand request, CancellationToken cancellationToken)
    {
        var issue = Issue.Create(request.Title, request.Description, request.Priority);
        
        await _repository.AddAsync(issue);

        await _cursorCacheService.AddOrUpdateIssueAsync(issue);

        return new Response<Guid>(issue.Id, "Issue created successfully.");
    }
}
