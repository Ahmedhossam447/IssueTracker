using System;

namespace IssueTracker.Core.Entities;

public class RefreshToken
{
    public Guid Id { get; private set; }
    public string Token { get; private set; } = null!;
    public string JwtId { get; private set; } = null!;
    public DateTime CreationDate { get; private set; }
    public DateTime ExpiryDate { get; private set; }
    public bool Used { get; private set; }
    public bool Invalidated { get; private set; }
    public string UserId { get; private set; } = null!;

    protected RefreshToken() { }

    public static RefreshToken Create(string token, string jwtId, string userId, DateTime expiryDate)
    {
        return new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = token,
            JwtId = jwtId,
            UserId = userId,
            CreationDate = DateTime.UtcNow,
            ExpiryDate = expiryDate,
            Used = false,
            Invalidated = false
        };
    }

    public void MarkAsUsed()
    {
        Used = true;
    }

    public void Invalidate()
    {
        Invalidated = true;
    }
}
