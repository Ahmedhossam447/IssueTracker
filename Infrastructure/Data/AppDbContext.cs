using System.Linq;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using IssueTracker.Core.Entities;
using IssueTracker.Infrastructure.Constraints;
using IssueTracker.Infrastructure.Identity;
using IssueTracker.Application.Interfaces;

namespace IssueTracker.Infrastructure.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    private readonly ICurrentUserService _currentUserService;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUserService currentUserService) : base(options)
    {
        _currentUserService = currentUserService;
    }

    public DbSet<Issue> Issues { get; set; } = null!;
    public DbSet<RefreshToken> RefreshTokens { get; set; } = null!;
    public DbSet<OutboxMessage> OutboxMessages { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.ApplyConfiguration(new IssueConstraint());
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var issueEntries = ChangeTracker.Entries<Issue>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified || e.State == EntityState.Deleted)
            .ToList();

        foreach (var entry in issueEntries)
        {
            var action = entry.State.ToString();
            var payload = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    IssueId = entry.Entity.Id,
                    Action = action,
                    UserEmail = _currentUserService.UserEmail,
                    Timestamp = DateTime.UtcNow      
                }
            );

            OutboxMessages.Add(
                new OutboxMessage
                {
                    Type = $"Issue.{action}",
                    Content = payload,
                    OccurredOnUtc = DateTime.UtcNow
                }
            );
        }
        
        return base.SaveChangesAsync(cancellationToken);
    }
}
