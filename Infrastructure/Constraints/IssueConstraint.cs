using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using IssueTracker.Core.Entities;

namespace IssueTracker.Infrastructure.Constraints;

public class IssueConstraint : IEntityTypeConfiguration<Issue>
{
    public void Configure(EntityTypeBuilder<Issue> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Title).IsRequired().HasMaxLength(200);
        builder.Property(i => i.Description).IsRequired();
        builder.Property(i => i.Status).HasConversion<string>(); // Saves the enum as a string
        builder.Property(i => i.Priority).HasConversion<string>();
    }
}
