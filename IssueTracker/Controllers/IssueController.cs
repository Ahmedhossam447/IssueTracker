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
using IssueTracker.Application.Queries.GetIssuesCursor;
using IssueTracker.Application.Queries.SearchIssues;
using IssueTracker.Application.Interfaces;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class IssueController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ActivityLogger.ActivityLoggerClient _activityClient; 
    private readonly ICursorCacheService _cursorCacheService;

    public IssueController(IMediator mediator, ActivityLogger.ActivityLoggerClient activityClient, ICursorCacheService cursorCacheService)
    {
        _mediator = mediator;
        _activityClient = activityClient;
        _cursorCacheService = cursorCacheService;
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
        bool useLeaderConnection;
        if (Request.Headers.ContainsKey("X-Recent-Write")) useLeaderConnection = true;
        else useLeaderConnection = false;

        int? minVersion = null;
        if (Request.Headers.TryGetValue("X-Min-Version", out var versionStr) && int.TryParse(versionStr, out var v))
        {
            minVersion = v;
        }

        var userEmail = User.FindFirstValue(ClaimTypes.Email) 
            ?? User.FindFirstValue("email");

        if (!string.IsNullOrEmpty(userEmail))
        {
            var serverVersion = await _cursorCacheService.GetUserLatestVersionAsync(userEmail);
            if (serverVersion.HasValue)
            {
                minVersion = Math.Max(minVersion ?? 0, serverVersion.Value);
            }
        }

        var response = await _mediator.Send(new GetIssueByIdQuery(id) { useLeaderConnection = useLeaderConnection, MinVersion = minVersion });
        
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

    [HttpGet("feed")]
    public async Task<IActionResult> GetFeed([FromQuery] long? cursor, [FromQuery] int pageSize = 10)
    {
        var response = await _mediator.Send(new GetIssuesCursorQuery(cursor, pageSize));
        return Ok(response);
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest("Search query 'q' cannot be empty.");
        }

        var response = await _mediator.Send(new SearchIssuesQuery(q, pageNumber, pageSize));
        return Ok(response);
    }
}

