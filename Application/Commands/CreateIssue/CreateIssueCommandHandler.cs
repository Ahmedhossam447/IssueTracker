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
    private readonly ICurrentUserService _currentUserService;

    public CreateIssueCommandHandler(
        IGenericRepository<Issue> repository, 
        ICursorCacheService cursorCacheService,
        ICurrentUserService currentUserService)
    {
        _repository = repository;
        _cursorCacheService = cursorCacheService;
        _currentUserService = currentUserService;
    }

    public async Task<Response<Guid>> Handle(CreateIssueCommand request, CancellationToken cancellationToken)
    {
        var issue = Issue.Create(request.Title, request.Description, request.Priority);
        
        await _repository.AddAsync(issue);

        await _cursorCacheService.AddOrUpdateIssueAsync(issue);

        var userEmail = _currentUserService.UserEmail;
        if (!string.IsNullOrEmpty(userEmail) && userEmail != "System / Anonymous")
        {
            await _cursorCacheService.SetUserLatestVersionAsync(userEmail, issue.Version);
        }

        return new Response<Guid>(issue.Id, "Issue created successfully.");
    }
}
