using System.Threading;
using System.Threading.Tasks;
using MediatR;
using IssueTracker.Application.Responses;
using IssueTracker.Application.Interfaces;
using IssueTracker.Application.DTOs;

namespace IssueTracker.Application.Commands.Auth;

public record LoginCommand(string Email, string Password) : IRequest<Response<AuthResultDto>>;

public class LoginCommandHandler : IRequestHandler<LoginCommand, Response<AuthResultDto>>
{
    private readonly IIdentityService _identityService;
    private readonly ITokenService _tokenService;

    public LoginCommandHandler(IIdentityService identityService, ITokenService tokenService)
    {
        _identityService = identityService;
        _tokenService = tokenService;
    }

    public async Task<Response<AuthResultDto>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var validPassword = await _identityService.CheckPasswordAsync(request.Email, request.Password);
        if (!validPassword)
        {
            return new Response<AuthResultDto>("Invalid email or password") { Succeeded = false };
        }

        var userId = await _identityService.GetUserIdAsync(request.Email);
        if (userId == null)
        {
            return new Response<AuthResultDto>("User not found") { Succeeded = false };
        }

        var jwt = _tokenService.GenerateJwtToken(userId, request.Email);
        var refreshToken = await _tokenService.GenerateRefreshTokenAsync(userId);

        var authResult = new AuthResultDto
        {
            Token = jwt,
            RefreshToken = refreshToken.Token
        };

        return new Response<AuthResultDto>(authResult, "Login successful");
    }
}
