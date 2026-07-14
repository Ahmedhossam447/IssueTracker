using System.Threading.Tasks;
using IssueTracker.Core.Entities;

namespace IssueTracker.Application.Interfaces;

public interface ITokenService
{
    string GenerateJwtToken(string userId, string email);
    Task<RefreshToken> GenerateRefreshTokenAsync(string userId);
    Task<RefreshToken?> GetRefreshTokenAsync(string token);
    Task UpdateRefreshTokenAsync(RefreshToken token);
}
