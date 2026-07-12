using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Interfaces;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Responses;

namespace IssueTracker.Application.Queries.GetAllIssues;

public class GetAllIssuesQueryHandler : IRequestHandler<GetAllIssuesQuery, PagedResponse<IEnumerable<IssueDto>>>
{
    private readonly IGenericRepository<Issue> _repository;

    public GetAllIssuesQueryHandler(IGenericRepository<Issue> repository)
    {
        _repository = repository;
    }

    public async Task<PagedResponse<IEnumerable<IssueDto>>> Handle(GetAllIssuesQuery request, CancellationToken cancellationToken)
    {
        var result = await _repository.GetPagedAsync(
            request.PageNumber, 
            request.PageSize,
            orderBy: q => q.OrderByDescending(i => i.CreatedAt));
        
        var issueDtos = result.Items.Select(i => new IssueDto
        {
            Id = i.Id,
            Title = i.Title,
            Description = i.Description,
            Status = i.Status,
            Priority = i.Priority,
            CreatedAt = i.CreatedAt,
            UpdatedAt = i.UpdatedAt
        }).ToList();

        return new PagedResponse<IEnumerable<IssueDto>>(
            issueDtos, 
            request.PageNumber, 
            request.PageSize, 
            result.TotalCount, 
            "Issues retrieved successfully.");
    }
}
