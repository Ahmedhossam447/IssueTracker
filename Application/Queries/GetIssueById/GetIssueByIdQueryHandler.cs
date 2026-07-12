using System.Threading;
using System.Threading.Tasks;
using MediatR;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Interfaces;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Responses;

namespace IssueTracker.Application.Queries.GetIssueById;

public class GetIssueByIdQueryHandler : IRequestHandler<GetIssueByIdQuery, Response<IssueDto>>
{
    private readonly IGenericRepository<Issue> _repository;

    public GetIssueByIdQueryHandler(IGenericRepository<Issue> repository)
    {
        _repository = repository;
    }

    public async Task<Response<IssueDto>> Handle(GetIssueByIdQuery request, CancellationToken cancellationToken)
    {
        // Notice we need an overload for GetByIdAsync that takes Guid in GenericRepository
        // We will fix that next if necessary, or just use the DbContext directly
        // However, IGenericRepository.GetByIdAsync(int id) expects an int. Let's assume we update it to Guid.
        var issue = await _repository.GetByIdAsync(request.Id);
        
        if (issue == null)
        {
            return new Response<IssueDto>("Issue not found.");
        }

        var issueDto = new IssueDto
        {
            Id = issue.Id,
            Title = issue.Title,
            Description = issue.Description,
            Status = issue.Status,
            Priority = issue.Priority,
            CreatedAt = issue.CreatedAt,
            UpdatedAt = issue.UpdatedAt
        };

        return new Response<IssueDto>(issueDto, "Issue retrieved successfully.");
    }
}
