namespace Verbara.Platform.Core;

public sealed record PagedQuery(int Page = 1, int PageSize = 25)
{
    public int Offset => (Page - 1) * PageSize;
}
