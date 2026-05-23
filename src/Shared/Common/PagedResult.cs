namespace EventWOS.Shared.Common;

/// <summary>Standard paginated response wrapper used across all list endpoints.</summary>
public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = new List<T>();
    public int TotalCount { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    public bool HasNextPage => PageNumber < TotalPages;
    public bool HasPreviousPage => PageNumber > 1;

    public static PagedResult<T> Create(IReadOnlyList<T> items, int totalCount, int pageNumber, int pageSize) =>
        new() { Items = items, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
}
