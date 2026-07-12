using System;

namespace IssueTracker.Application.Responses;

public class PagedResponse<T> : Response<T>
{
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalRecords { get; set; }
    public int TotalPages { get; set; }

    public PagedResponse(T data, int pageNumber, int pageSize, int totalRecords, string message = "") 
        : base(data, message)
    {
        PageNumber = pageNumber;
        PageSize = pageSize;
        TotalRecords = totalRecords;
        TotalPages = (int)Math.Ceiling((double)totalRecords / pageSize);
    }
}
