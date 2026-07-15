using MediatR;
using Microsoft.AspNetCore.Mvc;
using IssueTracker.Application.Commands.CreateIssue;
using IssueTracker.Application.Commands.UpdateIssue;
using IssueTracker.Application.Commands.DeleteIssue;
using IssueTracker.Application.Queries.GetAllIssues;
using IssueTracker.Application.Queries.GetIssueById;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
namespace IssueTracker.Controllers;
using IssueTracker.API.Protos;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class IssueController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ActivityLogger.ActivityLoggerClient _activityClient; 

    public IssueController(IMediator mediator, ActivityLogger.ActivityLoggerClient activityClient)
    {
        _mediator = mediator;
        _activityClient = activityClient;
    }

    [HttpPost]
    public async Task<IActionResult> CreateIssue([FromBody] CreateIssueCommand command)
    {
        var response = await _mediator.Send(command);
        
        if (!response.Succeeded)
        {
            return BadRequest(response);
        }

        return Ok(response);
    }

    [HttpGet]

    public async Task<IActionResult> GetAllIssues([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10)
    {
        var response = await _mediator.Send(new GetAllIssuesQuery(pageNumber, pageSize));
        return Ok(response);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetIssueById(Guid id)
    {
        var response = await _mediator.Send(new GetIssueByIdQuery(id));
        
        if (!response.Succeeded)
        {
            return NotFound(response);
        }

        return Ok(response);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateIssue(Guid id, [FromBody] UpdateIssueCommand command)
    {
        if (id != command.Id)
        {
            return BadRequest("ID in URL and ID in body do not match.");
        }

        var response = await _mediator.Send(command);
        if (response.Succeeded)
        {
            try
            {
                var userEmail = User.FindFirstValue(ClaimTypes.Email) 
                ?? User.FindFirstValue("email") 
                ?? "Anonymous";
                
                await _activityClient.LogActivityAsync(new ActivityRequest
                {
                    IssueId = id.ToString(),
                    Action = "Updated",
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    UserEmail = userEmail

                });
            }
            catch (Exception ex)
            {
                // Log the exception or handle it as needed
                Console.WriteLine($"Failed to log activity: {ex.Message}");
        }
        return Ok(response);
    }
    return BadRequest(response);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteIssue(Guid id)
    {
        var response = await _mediator.Send(new DeleteIssueCommand(id));
        return Ok(response);
    }
}

