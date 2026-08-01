using MediatR;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Interfaces;
using IssueTracker.Application.Responses;
using IssueTracker.Application.Interfaces;

namespace IssueTracker.Application.Commands.UpdateIssue;

public class UpdateIssueCommandHandler : IRequestHandler<UpdateIssueCommand, Response<Guid>>
{
    private readonly IGenericRepository<Issue> _repository;
    private readonly ICursorCacheService _cursorCacheService;
    public UpdateIssueCommandHandler(IGenericRepository<Issue> repository, ICursorCacheService cursorCacheService)
    {
        _repository = repository;
        _cursorCacheService = cursorCacheService;
    }

    public async Task<Response<Guid>> Handle(UpdateIssueCommand request, CancellationToken cancellationToken)
    {
        var issue = await _repository.GetByIdAsync(request.Id);
        if (issue == null)
        {
            throw new KeyNotFoundException($"Issue with ID {request.Id} was not found.");
        }

        issue.UpdateDetails(request.Title, request.Description, request.Priority);
        
        await _repository.UpdateAsync(issue);
        await _cursorCacheService.AddOrUpdateIssueAsync(issue);

        return new Response<Guid>(issue.Id, "Issue updated successfully.");
    }
}
