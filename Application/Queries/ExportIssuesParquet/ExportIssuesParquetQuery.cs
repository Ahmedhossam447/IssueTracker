using MediatR;

namespace IssueTracker.Application.Queries.ExportIssuesParquet;

public record ParquetExportResult(byte[] ParquetBytes, int RowCount, long RawJsonBytes);

public record ExportIssuesParquetQuery(int MaxRows = 50000) : IRequest<ParquetExportResult>;
