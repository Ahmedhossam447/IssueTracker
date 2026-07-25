using System.Data;
using IssueTracker.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace IssueTracker.Infrastructure.Data;

public class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _readOnlyConnectionString;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        _readOnlyConnectionString = configuration.GetConnectionString("ReadOnlyConnection") 
            ?? throw new InvalidOperationException("Connection string 'ReadOnlyConnection' not found.");
    }

    public IDbConnection GetConnection()
    {
        return new Npgsql.NpgsqlConnection(_readOnlyConnectionString);
    }
}