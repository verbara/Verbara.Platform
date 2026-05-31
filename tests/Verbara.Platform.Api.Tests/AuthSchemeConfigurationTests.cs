using Verbara.Platform.Api.Auth;
using Microsoft.AspNetCore.Http;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Regression coverage for v1.14.4 AUTH-002 fix — query-string token
/// extraction is scoped to a small set of legitimate paths
/// (SignalR / SSE / audio recording streams) and rejected everywhere
/// else.
/// </summary>
public sealed class AuthSchemeConfigurationTests
{
    [Theory]
    [InlineData("/hubs/platform")]
    [InlineData("/hubs/platform/negotiate")]
    [InlineData("/hubs/notifications/negotiate?id=abc")]
    [InlineData("/HUBS/Platform")] // case-insensitive
    public void IsQueryTokenPathAllowed_ShouldReturnTrue_ForSignalRHubPaths(string path)
    {
        AuthSchemeConfiguration.IsQueryTokenPathAllowed(new PathString(path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("/events/stream")]
    [InlineData("/events/stream/something")]
    [InlineData("/Events/Stream")] // case-insensitive
    [InlineData("/api/v1/events/stream")] // the ACTUAL deployed path (v1.MapSseEndpoints) — browser EventSource hits this
    [InlineData("/api/v2/events/stream")] // future versioned path
    public void IsQueryTokenPathAllowed_ShouldReturnTrue_ForSseStreamPaths(string path)
    {
        AuthSchemeConfiguration.IsQueryTokenPathAllowed(new PathString(path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("/api/v1/recordings/abc-123/stream")]
    [InlineData("/api/v2/recordings/session/stream")]
    [InlineData("/API/v1/Recordings/abc/Stream")] // case-insensitive
    public void IsQueryTokenPathAllowed_ShouldReturnTrue_ForRecordingStreamPaths(string path)
    {
        AuthSchemeConfiguration.IsQueryTokenPathAllowed(new PathString(path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("/api/v1/admin/users")]              // admin CRUD
    [InlineData("/api/v1/auth/login")]                // login
    [InlineData("/api/v1/conversations/abc")]         // domain endpoint
    [InlineData("/api/v1/recordings/abc")]            // recording metadata (NOT /stream)
    [InlineData("/api/v1/recordings/abc/download")]   // download (not /stream)
    [InlineData("/health")]                           // health check
    [InlineData("/swagger")]                          // docs
    [InlineData("/")]                                 // root
    public void IsQueryTokenPathAllowed_ShouldReturnFalse_ForNonStreamingPaths(string path)
    {
        AuthSchemeConfiguration.IsQueryTokenPathAllowed(new PathString(path)).Should().BeFalse();
    }

    [Fact]
    public void IsQueryTokenPathAllowed_ShouldReturnFalse_ForEmptyPath()
    {
        AuthSchemeConfiguration.IsQueryTokenPathAllowed(PathString.Empty).Should().BeFalse();
    }

    [Theory]
    [InlineData("/recordings/abc/stream")]   // missing /api/ prefix
    [InlineData("/api/recordings/abc/stream")] // missing version segment is OK because we only check "/api/" prefix; but this still has /recordings/ + /stream — allowed
    public void IsQueryTokenPathAllowed_RecordingStreamRequiresApiPrefix(string path)
    {
        // Path #1 lacks /api/ entirely → false. Path #2 has /api/recordings/abc/stream
        // — startsWith /api/, contains /recordings/, ends with /stream → true.
        // Two opposite expectations; assert each individually.
        var expected = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        AuthSchemeConfiguration.IsQueryTokenPathAllowed(new PathString(path)).Should().Be(expected);
    }
}
