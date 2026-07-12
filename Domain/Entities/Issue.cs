using IssueTracker.Core.Enums;

namespace IssueTracker.Core.Entities;

public class Issue
{
    public Guid Id { get; private set; }
    public string Title { get; private set; }
    public string Description { get; private set; }
    public IssueStatus Status { get; private set; }
    public IssuePriority Priority { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    protected Issue() 
    { 
        Title = null!;
        Description = null!;
    }
    public static Issue Create(string title, string description, IssuePriority priority)
    {
        return new Issue
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            Status = IssueStatus.Open,
            Priority = priority,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void UpdateStatus(IssueStatus newStatus)
    {
        Status = newStatus;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateDetails(string title, string description, IssuePriority priority)
    {
        Title = title;
        Description = description;
        Priority = priority;
        UpdatedAt = DateTime.UtcNow;
    }
}