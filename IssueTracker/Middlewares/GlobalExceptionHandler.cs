using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IssueTracker.Application.Exceptions;
using IssueTracker.Application.Responses;
using Microsoft.EntityFrameworkCore;

namespace IssueTracker.Middlewares;

public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "An unhandled exception has occurred: {Message}", exception.Message);

        var response = new Response<object>("An error occurred while processing your request.");
        var statusCode = StatusCodes.Status500InternalServerError;

        switch (exception)
        {
            case FluentValidation.ValidationException validationException:
                statusCode = StatusCodes.Status400BadRequest;
                response.Message = "Validation failed.";
                response.Errors = validationException.Errors.Select(e => e.ErrorMessage).ToList();
                break;
                
            case System.Collections.Generic.KeyNotFoundException:
                statusCode = StatusCodes.Status404NotFound;
                response.Message = "The requested resource was not found.";
                break;

            case System.UnauthorizedAccessException:
                statusCode = StatusCodes.Status401Unauthorized;
                response.Message = "You are not authorized to perform this action.";
                break;

            case ConcurrencyConflictException:
            case DbUpdateConcurrencyException:
                statusCode = StatusCodes.Status409Conflict;
                response.Message = "The resource was modified by another request. Please refresh and try again.";
                response.Errors = new System.Collections.Generic.List<string> { exception.Message };
                break;

            default:
                response.Errors = new System.Collections.Generic.List<string> { exception.Message };
                break;
        }

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);

        return true;
    }
}
