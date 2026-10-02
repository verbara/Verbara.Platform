using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Core.Impersonation;
using Verbara.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// An impersonation token is an Admin bearer token for ANOTHER tenant that lives 30 minutes, twice
/// the 15-minute access-token residual accepted for a suspension. Every request that presents one
/// is therefore checked against its impersonator's account status: suspending, deactivating or
/// deleting the admin ends the impersonation at once, not when the token expires.
/// </summary>
public sealed class ImpersonationTokenStatusTests : IClassFixture<AccountStatusApiFactory>, IAsyncLifetime
{
    private const string Customer = AccountStatusApiFactory.CustomerTenantId;
    private const string Platform = AccountStatusApiFactory.PlatformTenantId;

    private readonly AccountStatusApiFactory _factory;
    private readonly List<string> _startedSessions = [];

    public ImpersonationTokenStatusTests(AccountStatusApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Deactivated")]
    public async Task ImpersonationToken_ShouldStopAuthenticating_WhenImpersonatorLeavesActive(string newStatus)
    {
        var admin = NewPlatformAdmin();
        var impersonationToken = await StartImpersonationAsync(admin);
        (await ListTargetTenantUsersAsync(impersonationToken)).Should().Be(HttpStatusCode.OK,
            because: "the token works in the target tenant while its impersonator is Active");

        using var platformAdmin = PlatformAdminClient();
        (await platformAdmin.PutAsJsonAsync($"/api/v1/admin/users/{admin.UserId.Value}", new { status = newStatus }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await ListTargetTenantUsersAsync(impersonationToken)).Should().Be(HttpStatusCode.Unauthorized,
            because: "a suspended admin must not keep Admin access to another tenant for the rest of a 30-minute token");
    }

    [Fact]
    public async Task ImpersonationToken_ShouldStopAuthenticating_WhenImpersonatorIsDeleted()
    {
        var admin = NewPlatformAdmin();
        var impersonationToken = await StartImpersonationAsync(admin);
        (await ListTargetTenantUsersAsync(impersonationToken)).Should().Be(HttpStatusCode.OK);

        using var platformAdmin = PlatformAdminClient();
        (await platformAdmin.DeleteAsync($"/api/v1/admin/users/{admin.UserId.Value}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ListTargetTenantUsersAsync(impersonationToken)).Should().Be(HttpStatusCode.Unauthorized,
            because: "an impersonation token whose impersonator no longer exists has no one to act as");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // A token whose impersonator was suspended can no longer end its own session, and the session
    // store caps concurrent sessions per actor tenant: close what this test opened.
    public async Task DisposeAsync()
    {
        var sessions = _factory.Services.GetRequiredService<IImpersonationSessionStore>();
        foreach (var sessionId in _startedSessions)
        {
            await sessions.RevokeAsync(
                sessionId, ImpersonationSessionStatus.ManuallyRevoked, "test_cleanup", DateTimeOffset.UtcNow,
                CancellationToken.None);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private User NewPlatformAdmin() =>
        _factory.SaveUser($"impersonating-admin-{Guid.NewGuid():N}", Platform, UserRole.Admin, UserStatus.Active);

    private HttpClient PlatformAdminClient()
    {
        var admin = _factory.GetUser(AccountStatusApiFactory.PlatformAdminUserId, Platform)!;
        return _factory.CreateBearerClient(_factory.MintAccessToken(admin));
    }

    private async Task<string> StartImpersonationAsync(User admin)
    {
        using var client = _factory.CreateBearerClient(_factory.MintAccessToken(admin));
        using var response = await client.PostAsJsonAsync(
            "/api/v1/management/impersonate", new { targetTenantId = Customer, readOnly = false });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _startedSessions.Add(ReadString(body.RootElement, "sessionId"));
        return ReadString(body.RootElement, "accessToken");
    }

    private async Task<HttpStatusCode> ListTargetTenantUsersAsync(string impersonationToken)
    {
        using var client = _factory.CreateBearerClient(impersonationToken);
        using var response = await client.GetAsync("/api/v1/admin/users");
        return response.StatusCode;
    }

    private static string ReadString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.GetString()!;
        }

        throw new InvalidOperationException($"The impersonation response carries no '{name}' field.");
    }
}
