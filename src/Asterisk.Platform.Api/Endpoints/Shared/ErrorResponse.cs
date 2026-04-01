namespace Asterisk.Platform.Api.Endpoints.Shared;

internal sealed record ErrorResponse(string Error);
internal sealed record ErrorDetailResponse(string Error, IReadOnlyList<string> Details);
internal sealed record MessageResponse(string Message);
internal sealed record StatusUpdateResponse(string Id, string Status);
internal sealed record PagedDataResponse<T>(T[] Data, bool HasMore, int Page, int PageSize);
