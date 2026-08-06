using System.Data;
using IssueTracker.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace IssueTracker.Infrastructure.Data;

public class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _readOnlyConnectionString;
    private readonly string _LeaderConnectionString;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        _LeaderConnectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        _readOnlyConnectionString = configuration.GetConnectionString("ReadOnlyConnection") 
            ?? throw new InvalidOperationException("Connection string 'ReadOnlyConnection' not found.");
    }

    public IDbConnection GetConnection()
    {
        return new Npgsql.NpgsqlConnection(_readOnlyConnectionString);
    }

    public IDbConnection GetLeaderConnection()
    {
        return new Npgsql.NpgsqlConnection(_LeaderConnectionString);
    }
}