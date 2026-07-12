using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Interfaces;
using IssueTracker.Application.Responses;

namespace IssueTracker.Application.Commands.DeleteIssue;

public class DeleteIssueCommandHandler : IRequestHandler<DeleteIssueCommand, Response<Guid>>
{
    private readonly IGenericRepository<Issue> _repository;

    public DeleteIssueCommandHandler(IGenericRepository<Issue> repository)
    {
        _repository = repository;
    }

    public async Task<Response<Guid>> Handle(DeleteIssueCommand request, CancellationToken cancellationToken)
    {
        var issue = await _repository.GetByIdAsync(request.Id);
        if (issue == null)
        {
            throw new KeyNotFoundException($"Issue with ID {request.Id} was not found.");
        }
        
        await _repository.DeleteAsync(issue);

        return new Response<Guid>(issue.Id, "Issue deleted successfully.");
    }
}
