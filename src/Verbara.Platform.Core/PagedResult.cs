namespace Verbara.Platform.Core;

#pragma warning disable CA1000 // Do not declare static members on generic types
public sealed record PagedResult<T>
{
    public IReadOnlyList<T> Items { get; }
    public int TotalCount { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int TotalPages { get; }
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;

    public PagedResult(IReadOnlyList<T> items, int totalCount, int page, int pageSize)
    {
        Items = items;
        TotalCount = totalCount;
        Page = page;
        PageSize = pageSize;
        TotalPages = pageSize > 0 ? (int)Math.Ceiling((double)totalCount / pageSize) : 0;
    }

    public static PagedResult<T> Empty(int page, int pageSize) =>
        new([], totalCount: 0, page: page, pageSize: pageSize);
}
