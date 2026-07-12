using Microsoft.EntityFrameworkCore;
using IssueTracker.Core.Entities;
using IssueTracker.Infrastructure.Constraints;

namespace IssueTracker.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Issue> Issues { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.ApplyConfiguration(new IssueConstraint());
    }
}
