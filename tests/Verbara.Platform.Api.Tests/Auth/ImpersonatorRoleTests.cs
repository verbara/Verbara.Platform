using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// An impersonation token is an Admin bearer token for another tenant that lives 30 minutes, minted on
/// the strength of the role its impersonator held at the time. Every request that presents one is held
/// to that role: a role change ends the impersonation at once, and an access token issued before a
/// demotion, which still says Admin, cannot start a new one. A management key starts sessions on the
/// key's own authority, so the token it gets records its owner's role, whatever that role is.
/// </summary>
public sealed class ImpersonatorRoleTests : IClassFixture<ImpersonationApiFactory>, IAsyncLifetime
{
    private readonly ImpersonationApiFactory _factory;
    private readonly List<string> _startedSessions = [];

    public ImpersonatorRoleTests(ImpersonationApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("Supervisor")]
    [InlineData("Agent")]
    public async Task ImpersonationToken_ShouldStopAuthenticating_WhenImpersonatorIsDemoted(string newRole)
    {
        var admin = _factory.NewPlatformAdmin();
        var session = await StartAsync(admin);
        (await _factory.ListUsersAsync(session.AccessToken)).Should().Be(HttpStatusCode.OK,
            because: "the token works in the target tenant while its impersonator is still an Admin");

        await ChangeRoleAsync(admin, newRole);

        (await _factory.ListUsersAsync(session.AccessToken)).Should().Be(HttpStatusCode.Unauthorized,
            because: "a demoted admin must not keep Admin access to another tenant for the rest of a 30-minute token");
    }

    [Fact]
    public async Task ImpersonationToken_ShouldKeepAuthenticating_WhenImpersonatorKeepsItsRole()
    {
        var admin = _factory.NewPlatformAdmin();
        var session = await StartAsync(admin);

        await ChangeDisplayNameAsync(admin, "Renamed Admin");

        (await _factory.ListUsersAsync(session.AccessToken)).Should().Be(HttpStatusCode.OK,
            because: "only a change of role ends the impersonation, not any change to the impersonator");
    }

    [Fact]
    public async Task StartImpersonation_ShouldRefuse_WhenCallerWasDemotedAfterItsAccessTokenWasIssued()
    {
        var admin = _factory.NewPlatformAdmin();
        var accessToken = _factory.MintAccessToken(admin);
        await ChangeRoleAsync(admin, "Agent");

        using var client = _factory.CreateBearerClient(accessToken);
        using var response = await client.PostAsJsonAsync(
            "/api/v1/management/impersonate",
            new { targetTenantId = AccountStatusApiFactory.CustomerTenantId, readOnly = false });
        await TrackSessionAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "the access token still says Admin, but its owner no longer is one");
    }

    [Fact]
    public async Task ManagementKey_ShouldStartAWorkingImpersonation_WhenItsOwnerIsNotAnAdmin()
    {
        var owner = NewPlatformUser(UserRole.Supervisor);

        var accessToken = await StartWithManagementKeyAsync(_factory.NewManagementKey(owner));

        (await _factory.ListUsersAsync(accessToken)).Should().Be(HttpStatusCode.OK,
            because: "a management key starts a session on the key's authority; its token records the owner's role as it is");
    }

    [Fact]
    public async Task ImpersonationToken_ShouldStopAuthenticating_WhenManagementKeyOwnerRoleChanges()
    {
        var owner = NewPlatformUser(UserRole.Supervisor);
        var accessToken = await StartWithManagementKeyAsync(_factory.NewManagementKey(owner));
        (await _factory.ListUsersAsync(accessToken)).Should().Be(HttpStatusCode.OK);

        await ChangeRoleAsync(owner, "Agent");

        (await _factory.ListUsersAsync(accessToken)).Should().Be(HttpStatusCode.Unauthorized,
            because: "the token is held to the role its impersonator had when it was minted");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // The session store caps concurrent sessions per actor tenant: close whatever a test left open.
    public Task DisposeAsync() => _factory.CloseSessionsAsync(_startedSessions);

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private User NewPlatformUser(UserRole role) =>
        _factory.SaveUser($"impersonating-{role}-{Guid.NewGuid():N}".ToLowerInvariant(),
            AccountStatusApiFactory.PlatformTenantId, role, UserStatus.Active);

    private async Task<StartedImpersonation> StartAsync(User admin)
    {
        var session = await _factory.StartImpersonationAsync(admin);
        _startedSessions.Add(session.SessionId);
        return session;
    }

    private async Task<string> StartWithManagementKeyAsync(string managementKey)
    {
        using var client = _factory.CreateBearerClient(managementKey);
        using var response = await client.PostAsJsonAsync(
            "/api/v1/management/impersonate",
            new { targetTenantId = AccountStatusApiFactory.CustomerTenantId, readOnly = false });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the management key must start the impersonation ({body})");

        await TrackSessionAsync(response);
        using var json = JsonDocument.Parse(body);
        return ReadString(json.RootElement, "accessToken");
    }

    private async Task ChangeRoleAsync(User user, string newRole)
    {
        using var platformAdmin = _factory.CreateBearerClient(_factory.PlatformAdminToken());
        using var response = await platformAdmin.PutAsJsonAsync(
            $"/api/v1/admin/users/{user.UserId.Value}", new { role = newRole });
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: await response.Content.ReadAsStringAsync());
    }

    private async Task ChangeDisplayNameAsync(User user, string displayName)
    {
        using var platformAdmin = _factory.CreateBearerClient(_factory.PlatformAdminToken());
        using var response = await platformAdmin.PutAsJsonAsync(
            $"/api/v1/admin/users/{user.UserId.Value}", new { displayName });
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: await response.Content.ReadAsStringAsync());
    }

    // A session a test opens, even one it expected to be refused, is closed when the test ends.
    private async Task TrackSessionAsync(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            return;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _startedSessions.Add(ReadString(json.RootElement, "sessionId"));
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
