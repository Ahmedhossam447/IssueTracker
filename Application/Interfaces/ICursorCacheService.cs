using IssueTracker.Application.DTOs;
using IssueTracker.Core.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace IssueTracker.Application.Interfaces
{
    public interface ICursorCacheService
    {

        Task<(IEnumerable<IssueDto> Issues, long? NextCursor)> GetIssueByCursorAsync (int PageSize,long? TimeStamp);
        Task<bool> AddOrUpdateIssueAsync(Issue issue);
    }
}
