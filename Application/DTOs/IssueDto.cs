using System;
using IssueTracker.Core.Enums;

namespace IssueTracker.Application.DTOs;

public class IssueDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IssueStatus Status { get; set; }
    public IssuePriority Priority { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
