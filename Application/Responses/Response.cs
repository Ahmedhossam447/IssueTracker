using System.Collections.Generic;

namespace IssueTracker.Application.Responses;

public class Response<T>
{
    public bool Succeeded { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
    public T? Data { get; set; }

    public Response()
    {
    }

    public Response(T data, string message = "")
    {
        Succeeded = true;
        Message = message;
        Data = data;
    }

    public Response(string message)
    {
        Succeeded = false;
        Message = message;
    }
}
