using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using IssueTracker.Core.Entities;
using IssueTracker.Core.Interfaces;
using IssueTracker.Infrastructure.Data;

namespace IssueTracker.Infrastructure.Repositories;

public class OutboxRepository : IOutboxRepository
{
    private readonly AppDbContext _context;

    public OutboxRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(OutboxMessage message)
    {
        await _context.OutboxMessages.AddAsync(message);
    }

    public async Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesAsync(int batchSize = 20)
    {
        return await _context.OutboxMessages
            .FromSqlRaw(
                @"SELECT * FROM ""OutboxMessages"" WHERE ""ProcessedOnUtc"" IS NULL ORDER BY ""OccurredOnUtc"" LIMIT {0} FOR UPDATE SKIP LOCKED",
                batchSize)
            .ToListAsync();
    }

    public async Task MarkAsProcessedAsync(OutboxMessage message)
    {
        message.ProcessedOnUtc = DateTime.UtcNow;
        _context.OutboxMessages.Update(message);
        await _context.SaveChangesAsync();
    }
}
