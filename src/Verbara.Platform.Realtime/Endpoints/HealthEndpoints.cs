using System.Text.Json.Serialization;

namespace Verbara.Platform.Realtime.Endpoints;

/// <summary>
/// Health endpoints exposed by Verbara.Platform.Realtime.
/// Phase A.1 ships <c>/health</c> only. Phase A.2+A.3 adds
/// <c>/health/ready</c> that chequea Redis backplane + Pro.Cluster
/// connectivity.
/// </summary>
public static class HealthEndpoints
{
    public static void MapHealthEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () =>
            Results.Json(
                new HealthResponse("healthy", DateTimeOffset.UtcNow),
                HealthJsonContext.Default.HealthResponse));
    }
}

public sealed record HealthResponse(string Status, DateTimeOffset At);

[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class HealthJsonContext : JsonSerializerContext;
