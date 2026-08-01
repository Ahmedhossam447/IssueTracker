namespace IssueTracker.Application.Responses;

public class CursorResponse<T>
{
    public T Data { get; set; }
    public long? NextCursor { get; set; }

    public CursorResponse(T data, long? nextCursor)
    {
        Data = data;
        NextCursor = nextCursor;
    }
}
