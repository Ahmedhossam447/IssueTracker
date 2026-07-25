using System.Collections.Generic;
using System.Threading.Tasks;
using IssueTracker.Core.Entities;

namespace IssueTracker.Core.Interfaces;

public interface IOutboxRepository
{
    Task AddAsync(OutboxMessage message);
    Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesAsync(int batchSize = 20);
    Task MarkAsProcessedAsync(OutboxMessage message);
}
