using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IssueTracker.Application.Commands.UpdateIssue;
using IssueTracker.Application.DTOs;
using IssueTracker.Application.Interfaces;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Enums;
using IssueTracker.Core.Interfaces;
using IssueTracker.Infrastructure.Services;
using StackExchange.Redis;

namespace IssueTracker.SimulationTests;

// ============================================================================
// 4. SIMULATED POSTGRESQL CLUSTER (Leader + Async Replicas)
// ============================================================================
public class SimulatedPostgres : IGenericRepository<Issue>
{
    private readonly SimulationEngine _sim;
    private readonly Dictionary<Guid, Issue> _leaderTable = new();

    public SimulatedPostgres(SimulationEngine sim)
    {
        _sim = sim;
    }

    public Issue SeedIssue(string title, string description, IssuePriority priority)
    {
        var issue = Issue.Create(title, description, priority);
        _leaderTable[issue.Id] = CloneIssue(issue);
        return CloneIssue(issue);
    }

    public Issue GetLeaderState(Guid id) => CloneIssue(_leaderTable[id]);

    public Task<Issue?> GetByIdAsync(Guid id)
    {
        _sim.Clock.AdvanceMs(5);
        _leaderTable.TryGetValue(id, out var issue);
        return Task.FromResult(issue != null ? CloneIssue(issue) : null);
    }

    public Task<IEnumerable<Issue>> GetAllAsync()
    {
        _sim.Clock.AdvanceMs(5);
        IEnumerable<Issue> list = _leaderTable.Values.Select(CloneIssue).ToList();
        return Task.FromResult(list);
    }

    public Task<(IEnumerable<Issue> Items, int TotalCount)> GetPagedAsync(
        int pageNumber,
        int pageSize,
        Func<IQueryable<Issue>, IOrderedQueryable<Issue>>? orderBy = null)
    {
        var items = _leaderTable.Values.Select(CloneIssue).ToList();
        return Task.FromResult(((IEnumerable<Issue>)items, items.Count));
    }

    public Task AddAsync(Issue entity)
    {
        _sim.Clock.AdvanceMs(10);
        _leaderTable[entity.Id] = CloneIssue(entity);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Issue entity)
    {
        _sim.Clock.AdvanceMs(10);
        if (_leaderTable.TryGetValue(entity.Id, out var current) && entity.Version != current.Version + 1)
        {
            throw new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException(
                $"Simulated Postgres Concurrency Conflict: Expected Version {current.Version}, got update from stale Version {entity.Version - 1}.");
        }
        _leaderTable[entity.Id] = CloneIssue(entity);
        _sim.Log("PG-LEADER", $"Committed Issue '{entity.Title}' -> Version {entity.Version}");
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Issue entity)
    {
        _leaderTable.Remove(entity.Id);
        return Task.CompletedTask;
    }

    // Helper to clone entity so in-memory references don't bypass serialization
    public static Issue CloneIssue(Issue src)
    {
        var copy = Issue.Create(src.Title, src.Description, src.Priority);
        typeof(Issue).GetProperty(nameof(Issue.Id))!.SetValue(copy, src.Id);
        typeof(Issue).GetProperty(nameof(Issue.Status))!.SetValue(copy, src.Status);
        typeof(Issue).GetProperty(nameof(Issue.CreatedAt))!.SetValue(copy, src.CreatedAt);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))!.SetValue(copy, src.UpdatedAt);
        typeof(Issue).GetProperty(nameof(Issue.Version))!.SetValue(copy, src.Version);
        return copy;
    }
}
