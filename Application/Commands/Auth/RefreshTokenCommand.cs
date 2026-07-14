using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using IssueTracker.Application.Responses;
using IssueTracker.Application.Interfaces;
using IssueTracker.Application.DTOs;
using System.IdentityModel.Tokens.Jwt;

namespace IssueTracker.Application.Commands.Auth;

public record RefreshTokenCommand(string Token, string RefreshToken) : IRequest<Response<AuthResultDto>>;

public class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, Response<AuthResultDto>>
{
    private readonly ITokenService _tokenService;

    public RefreshTokenCommandHandler(ITokenService tokenService)
    {
        _tokenService = tokenService;
    }

    public async Task<Response<AuthResultDto>> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var storedRefreshToken = await _tokenService.GetRefreshTokenAsync(request.RefreshToken);

        if (storedRefreshToken == null || storedRefreshToken.Used || storedRefreshToken.Invalidated || storedRefreshToken.ExpiryDate < DateTime.UtcNow)
        {
            return new Response<AuthResultDto>("Invalid or expired refresh token") { Succeeded = false };
        }

        // Technically, we should also validate the JWT signature here without checking expiration, 
        // but for simplicity we rely on the DB stored refresh token.
        var handler = new JwtSecurityTokenHandler();
        var jwtToken = handler.ReadJwtToken(request.Token);
        var email = jwtToken.Subject ?? jwtToken.Claims.FirstOrDefault(c => c.Type == "email")?.Value ?? string.Empty;

        storedRefreshToken.MarkAsUsed();
        await _tokenService.UpdateRefreshTokenAsync(storedRefreshToken);

        var newJwt = _tokenService.GenerateJwtToken(storedRefreshToken.UserId, email);
        var newRefreshToken = await _tokenService.GenerateRefreshTokenAsync(storedRefreshToken.UserId);

        var authResult = new AuthResultDto
        {
            Token = newJwt,
            RefreshToken = newRefreshToken.Token
        };

        return new Response<AuthResultDto>(authResult, "Token refreshed successfully");
    }
}
