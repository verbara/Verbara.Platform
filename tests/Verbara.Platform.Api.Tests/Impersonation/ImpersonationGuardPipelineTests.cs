using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using Verbara.Platform.Api.Tests.Auth;
using Verbara.Platform.Core.Impersonation;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Impersonation;

/// <summary>
/// What an impersonation session may not do, over the real <c>Program.cs</c> pipeline with tokens this
/// host's own <c>JwtTokenService</c> mints: the operations no impersonation session may perform, every
/// write but ending the session in a read-only one, and permission gates, which an impersonation token
/// passes only with a permission minted into it. A test that sets <c>HttpContext.User</c> by hand cannot
/// see where a middleware runs relative to authentication; these requests go through authentication.
/// </summary>
public sealed class ImpersonationGuardPipelineTests : IClassFixture<ImpersonationApiFactory>, IAsyncLifetime
{
    private const string DuringImpersonationBody = "Operation not allowed during impersonation";
    private const string ReadOnlyBody = "Operation not allowed in read-only impersonation mode";
    private const string ContactJson = """{"firstName":"Impersonation","lastName":"Probe"}""";
    private const string FeatureJson = """{"enabled":false,"provider":null,"credentials":null}""";

    // A read-only token of an impersonator who holds these reads; a full token of one who also holds a
    // permission-gated write.
    private static readonly string[] ReadOnlyPermissions = ["contacts:contact:view", "users:user:view"];
    private static readonly string[] FullPermissions =
        ["contacts:contact:view", "users:user:view", "features:agent-assist:manage"];

    private readonly ImpersonationApiFactory _factory;
    private readonly List<string> _startedSessions = [];

    public ImpersonationGuardPipelineTests(ImpersonationApiFactory factory) => _factory = factory;

    // ─── read-only ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("POST", "/api/v1/contacts", ContactJson)]                         // authentication only
    [InlineData("POST", "/api/v1.0/contacts", ContactJson)]                       // the same endpoint, version alias
    [InlineData("POST", "/api/contacts", ContactJson)]                            // the same endpoint, unversioned
    [InlineData("PUT", "/api/v1/admin/features/agent-assist", FeatureJson)]       // permission-gated
    [InlineData("POST", "/api/v1/admin/queues", """{"name":"impersonation-queue"}""")] // role-gated (AdminOnly)
    [InlineData("POST", "/api/v1/admin/gdpr/export", """{"contactId":"c-1"}""")]  // a personal-data export
    public async Task ReadOnlyImpersonation_ShouldRefuseWrite_WhenAnyWriteIsAttempted(string method, string path, string? json)
    {
        var token = _factory.MintImpersonationToken(_factory.NewPlatformAdmin(), ReadOnlyPermissions, readOnly: true);

        var (status, body) = await SendAsync(token, method, path, json);

        status.Should().Be(HttpStatusCode.Forbidden, because: $"a read-only session must not write ({body})");
        body.Should().Contain(ReadOnlyBody);
    }

    [Fact]
    public async Task ReadOnlyImpersonation_ShouldRefuseWrite_WhenNoEndpointMatches()
    {
        var token = _factory.MintImpersonationToken(_factory.NewPlatformAdmin(), ReadOnlyPermissions, readOnly: true);

        var (status, body) = await SendAsync(token, "POST", "/api/v1/no-such-endpoint", "{}");

        status.Should().Be(HttpStatusCode.Forbidden, because: "the read-only rule fails closed on a request it cannot classify");
        body.Should().Contain(ReadOnlyBody);
    }

    [Fact]
    public async Task ReadOnlyImpersonation_ShouldAllowRead_WhenMethodIsGet()
    {
        var token = _factory.MintImpersonationToken(_factory.NewPlatformAdmin(), ReadOnlyPermissions, readOnly: true);

        var (status, body) = await SendAsync(token, "GET", "/api/v1/contacts");

        status.Should().Be(HttpStatusCode.OK, because: $"a read-only session reads ({body})");
    }

    [Fact]
    public async Task ReadOnlyImpersonation_ShouldEndItsSession_WhenItEndsTheImpersonation()
    {
        var session = await StartAsync(_factory.NewPlatformAdmin(), readOnly: true);

        (await _factory.EndImpersonationAsync(session.AccessToken)).Should().Be(HttpStatusCode.NoContent,
            because: "ending the session is the one write a read-only session may make");
        (await _factory.SessionStatusAsync(session.SessionId)).Should().Be(ImpersonationSessionStatus.Completed);
    }

    [Fact]
    public async Task ReadOnlyImpersonation_ShouldRefusePermissionGatedRead_WhenPermissionWasNotMinted()
    {
        var token = _factory.MintImpersonationToken(_factory.NewPlatformAdmin(), ReadOnlyPermissions, readOnly: true);

        var (status, body) = await SendAsync(token, "GET", "/api/v1/admin/features/agent-assist");

        status.Should().Be(HttpStatusCode.Forbidden,
            because: "the token's Admin role does not stand in for features:agent-assist:manage, which it does not carry");
        body.Should().NotContain(ReadOnlyBody, because: "a read is refused by the permission gate, not by the read-only rule");
    }

    [Fact]
    public async Task StartImpersonation_ShouldMintCreditRead_WhenSessionIsReadOnly()
    {
        var session = await StartAsync(_factory.NewPlatformAdmin(), readOnly: true);

        var minted = new JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken).Claims
            .Where(c => c.Type == "permissions")
            .Select(c => c.Value)
            .ToList();
        minted.Should().Contain("billing:credits:read",
            because: "the credit balance is a read, so a read-only session keeps the permission that gates it");
        minted.Should().NotContain("features:agent-assist:manage",
            because: "a manage permission never enters a read-only token, even when the impersonator holds it");

        var (status, body) = await SendAsync(session.AccessToken, "GET", "/api/v1/admin/credit-ledger/balance");
        status.Should().Be(HttpStatusCode.OK, because: $"the minted read passes its permission gate ({body})");
    }

    // ─── during every impersonation ───────────────────────────────────────────

    [Theory]
    [InlineData("POST", "/api/v1/management/impersonate")]
    [InlineData("POST", "/api/management/impersonate")]
    [InlineData("POST", "/api/v1/setup")]
    [InlineData("POST", "/api/setup")]
    [InlineData("DELETE", "/api/v1/management/tenants/acct-status-customer")]
    [InlineData("PUT", "/api/v1/management/system/settings")]
    [InlineData("PUT", "/api/v1/management/system/license")]
    [InlineData("POST", "/api/v1/auth/change-password")]
    [InlineData("POST", "/api/v1.0/auth/change-password")]
    [InlineData("POST", "/api/v1/auth/change-password/")]
    [InlineData("POST", "/api/v1/auth/mfa/setup")]
    [InlineData("POST", "/api/v1/auth/mfa/confirm")]
    [InlineData("DELETE", "/api/v1/auth/mfa")]
    [InlineData("POST", "/api/v1/auth/mfa/recovery-codes/regenerate")]
    [InlineData("POST", "/api/v1/auth/sessions/revoke-others")]
    [InlineData("DELETE", "/api/v1/auth/sessions/some-token-id")]
    [InlineData("POST", "/api/v1/profile/security/mfa/enroll/init")]
    [InlineData("POST", "/api/v1/profile/security/mfa/enroll/verify")]
    [InlineData("POST", "/api/v1/profile/security/mfa/enroll/complete")]
    [InlineData("POST", "/api/v1/profile/security/recovery-codes/regenerate")]
    [InlineData("POST", "/api/v1/profile/security/sessions/some-token-id/revoke")]
    [InlineData("DELETE", "/api/v1/admin/auth/sessions/some-session-id")]
    [InlineData("DELETE", "/api/v1.0/admin/auth/sessions/some-session-id")]
    [InlineData("DELETE", "/api/v1/admin/auth/sessions/by-user/some-user")]
    [InlineData("DELETE", "/api/v1.0/admin/auth/sessions/by-user/some-user")]
    public async Task FullImpersonation_ShouldRefuseOperation_WhenOperationIsBlockedDuringImpersonation(string method, string path)
    {
        var token = _factory.MintImpersonationToken(_factory.NewPlatformAdmin(), FullPermissions, readOnly: false);

        var (status, body) = await SendAsync(token, method, path, method == "DELETE" ? null : "{}");

        status.Should().Be(HttpStatusCode.Forbidden, because: $"no impersonation session may do this ({body})");
        body.Should().Contain(DuringImpersonationBody);
    }

    [Fact]
    public async Task FullImpersonation_ShouldAllowWrite_WhenOperationIsNotBlocked()
    {
        var token = _factory.MintImpersonationToken(_factory.NewPlatformAdmin(), FullPermissions, readOnly: false);

        var (status, body) = await SendAsync(token, "POST", "/api/v1/contacts", ContactJson);

        status.Should().Be(HttpStatusCode.Created, because: $"a full session writes in the target tenant ({body})");
    }

    [Fact]
    public async Task FullImpersonation_ShouldRefusePermissionGatedWrite_WhenPermissionWasNotMinted()
    {
        var token = _factory.MintImpersonationToken(_factory.NewPlatformAdmin(), ["contacts:contact:view"], readOnly: false);

        var (status, body) = await SendAsync(token, "PUT", "/api/v1/admin/features/agent-assist", FeatureJson);

        status.Should().Be(HttpStatusCode.Forbidden,
            because: "a full session holds the impersonator's permissions, not every permission its Admin role would grant");
        body.Should().NotContain(DuringImpersonationBody);
    }

    [Fact]
    public async Task FullImpersonation_ShouldAllowPermissionGatedWrite_WhenPermissionWasMinted()
    {
        var token = _factory.MintImpersonationToken(_factory.NewPlatformAdmin(), FullPermissions, readOnly: false);

        var (status, body) = await SendAsync(token, "PUT", "/api/v1/admin/features/agent-assist", FeatureJson);

        status.Should().Be(HttpStatusCode.OK, because: $"the minted permission passes its gate ({body})");
    }

    [Fact]
    public async Task FullImpersonation_ShouldRefusePartnerDelegatedPermissionGate_WhenPermissionWasNotMinted()
    {
        var partner = await _factory.NewPartnerActorAsync(impersonationTimeoutMinutes: 30);
        var token = _factory.MintImpersonationToken(
            _factory.NewPlatformAdmin(), FullPermissions, readOnly: false, targetTenantId: partner.Admin.TenantId.Value);

        var (status, _) = await SendAsync(token, "GET", "/api/v1/management/mfa/users");

        status.Should().Be(HttpStatusCode.Forbidden,
            because: "impersonating a Partner tenant must not pass its system:mfa:manage gate on the token's Admin role");
    }

    [Fact]
    public async Task FullImpersonation_ShouldPassPartnerDelegatedPermissionGate_WhenPermissionWasMinted()
    {
        var partner = await _factory.NewPartnerActorAsync(impersonationTimeoutMinutes: 30);
        var token = _factory.MintImpersonationToken(
            _factory.NewPlatformAdmin(), ["system:mfa:manage"], readOnly: false, targetTenantId: partner.Admin.TenantId.Value);

        var (status, body) = await SendAsync(token, "GET", "/api/v1/management/mfa/users");

        status.Should().Be(HttpStatusCode.OK, because: $"the minted permission passes the gate ({body})");
    }

    // ─── not impersonating ────────────────────────────────────────────────────

    [Fact]
    public async Task TenantAdmin_ShouldNotBeGuarded_WhenNotImpersonating()
    {
        var customerAdmin = _factory.GetUser(AccountStatusApiFactory.CustomerAdminUserId, AccountStatusApiFactory.CustomerTenantId)!;

        var (status, body) = await SendAsync(
            _factory.MintAccessToken(customerAdmin), "DELETE", "/api/v1/admin/auth/sessions/some-session-id");

        body.Should().NotContain(DuringImpersonationBody);
        status.Should().NotBe(HttpStatusCode.Forbidden, because: "an admin revoking a session in its own tenant is not impersonating");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // The session store caps concurrent sessions per actor tenant: close whatever a test left open.
    public Task DisposeAsync() => _factory.CloseSessionsAsync(_startedSessions);

    private async Task<StartedImpersonation> StartAsync(User admin, bool readOnly)
    {
        var session = await _factory.StartImpersonationAsync(admin, readOnly: readOnly);
        _startedSessions.Add(session.SessionId);
        return session;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(string bearer, string method, string path, string? json = null)
    {
        using var client = _factory.CreateBearerClient(bearer);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
