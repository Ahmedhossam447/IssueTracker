using System.Threading.Tasks;

namespace IssueTracker.Application.Interfaces;

public interface IIdentityService
{
    Task<(bool Success, string[] Errors)> RegisterUserAsync(string email, string password, string firstName, string lastName);
    Task<bool> CheckPasswordAsync(string email, string password);
    Task<string?> GetUserIdAsync(string email);
}
