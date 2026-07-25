using System.Data;
namespace IssueTracker.Core.Interfaces;

public interface ISqlConnectionFactory
{
    IDbConnection GetConnection();
}