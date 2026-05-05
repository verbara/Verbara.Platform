namespace Verbara.Platform.Identity;

public sealed record AuthEventQuery(
    string? UserId = null,
    string? EventType = null,
    DateTimeOffset? StartDate = null,
    DateTimeOffset? EndDate = null,
    int Page = 1,
    int PageSize = 50);
