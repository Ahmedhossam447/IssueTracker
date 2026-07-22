using System.Threading;
using System.Threading.Tasks;
using MediatR;
using IssueTracker.Application.Responses;
using IssueTracker.Application.Interfaces;
using System.Linq;

namespace IssueTracker.Application.Commands.Auth;

public record RegisterCommand(string Email, string Password, string FirstName, string LastName) : IRequest<Response<string>>;

public class RegisterCommandHandler : IRequestHandler<RegisterCommand, Response<string>>
{
    private readonly IIdentityService _identityService;

    public RegisterCommandHandler(IIdentityService identityService)
    {
        _identityService = identityService;
    }

    public async Task<Response<string>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var result = await _identityService.RegisterUserAsync(request.Email, request.Password, request.FirstName, request.LastName);

        if (!result.Success)
        {
            return new Response<string>("User registration failed")
            {
                Succeeded = false,
                Errors = result.Errors.ToList()
            };
        }

        return new Response<string>("User registered successfully") { Succeeded = true };
    }
}
