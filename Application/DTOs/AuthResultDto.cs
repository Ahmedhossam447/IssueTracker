using System.Text.Json.Serialization;

namespace IssueTracker.Application.DTOs;

public class AuthResultDto
{
    public string Token { get; set; } = string.Empty;
    
    [JsonIgnore]
    public string RefreshToken { get; set; } = string.Empty;
}
