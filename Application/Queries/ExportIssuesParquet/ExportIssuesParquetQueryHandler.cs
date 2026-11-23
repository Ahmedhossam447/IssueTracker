using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MediatR;
using Parquet;
using Parquet.Serialization;
using IssueTracker.Application.DTOs;
using IssueTracker.Core.Interfaces;

namespace IssueTracker.Application.Queries.ExportIssuesParquet;

/// <summary>
/// DDIA Ch. 1 & 4 (OLTP vs OLAP & Columnar Storage):
/// Queries the PostgreSQL Read Replica (Port 5433) sorted by (Priority, Status, CreatedAt)
/// and writes a Snappy-compressed column-oriented Apache Parquet file.
/// </summary>
public class ExportIssuesParquetQueryHandler : IRequestHandler<ExportIssuesParquetQuery, ParquetExportResult>
{
    private readonly ISqlConnectionFactory _sqlConnectionFactory;

    public ExportIssuesParquetQueryHandler(ISqlConnectionFactory sqlConnectionFactory)
    {
        _sqlConnectionFactory = sqlConnectionFactory;
    }

    private sealed class ParquetIssueRow
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Priority { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public int Version { get; init; }
    }

    public async Task<ParquetExportResult> Handle(ExportIssuesParquetQuery request, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT ""Id"", ""Title"", ""Description"", ""Status"", ""Priority"", ""CreatedAt"", ""UpdatedAt"", ""Version""
            FROM ""Issues""
            ORDER BY ""Priority"", ""Status"", ""CreatedAt"" DESC
            LIMIT @MaxRows";

        using var connection = _sqlConnectionFactory.GetConnection();
        var issues = (await connection.QueryAsync<IssueDto>(sql, new { MaxRows = request.MaxRows })).ToList();

        long rawJsonBytes = JsonSerializer.SerializeToUtf8Bytes(issues).LongLength;

        var rows = issues.Select(i => new ParquetIssueRow
        {
            Id = i.Id.ToString(),
            Title = i.Title,
            Description = i.Description,
            Status = i.Status.ToString(),
            Priority = i.Priority.ToString(),
            CreatedAt = DateTime.SpecifyKind(i.CreatedAt, DateTimeKind.Utc),
            UpdatedAt = i.UpdatedAt.HasValue ? DateTime.SpecifyKind(i.UpdatedAt.Value, DateTimeKind.Utc) : null,
            Version = i.Version
        }).ToList();

        using var ms = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, ms, cancellationToken: cancellationToken);

        return new ParquetExportResult(ms.ToArray(), issues.Count, rawJsonBytes);
    }
}
