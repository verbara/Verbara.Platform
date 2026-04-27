using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Asterisk.Platform.Api.Tests.Endpoints.Security;

/// <summary>
/// R5.4 S5.9 — endpoint integration coverage for the JWT signing-key rotation
/// admin surface. Verifies:
/// <list type="bullet">
///   <item>Unauthenticated requests are rejected with 401.</item>
///   <item>Authenticated requests without the platform-admin double-lock are
///         rejected with 403 (the <c>NonAdminAuthenticatedApiFactory</c> seeds
///         an Agent-role tenant outside the host).</item>
///   <item>Platform-admin requests succeed with 200 and emit the
///         <c>security.jwt.key_rotated</c> audit entry.</item>
/// </list>
/// Pairs with <see cref="PlatformAdminApiFactory"/> to mirror the same
/// host-tenant + Management API key shape used by the R5.2 MFA / Audit /
/// Retention admin endpoints.
/// </summary>
public sealed class JwtKeyEndpointsTests : IClassFixture<PlatformAdminApiFactory>
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    public JwtKeyEndpointsTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task RotateKey_ShouldReturn401_WhenUnauthenticated()
    {
        using var anonymous = _factory.CreateAnonymousClient();

        var response = await anonymous.PostAsync(
            "/api/v1/management/security/jwt/rotate-key",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RotateKey_ShouldReturn403_WhenAuthenticatedButMissingPermission()
    {
        // NonAdminAuthenticatedApiFactory seeds an Agent-role user on a
        // non-host tenant, so PlatformAdminAuthorizationHandler refuses both
        // the host-tenant gate and the security.jwt.rotate permission gate.
        await using var nonAdmin = new NonAdminAuthenticatedApiFactory();
        using var nonAdminClient = nonAdmin.CreateAuthenticatedClient();

        var response = await nonAdminClient.PostAsync(
            "/api/v1/management/security/jwt/rotate-key",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RotateKey_ShouldReturn200AndEmitAudit_WhenAuthorized()
    {
        var response = await _client.PostAsync(
            "/api/v1/management/security/jwt/rotate-key",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<RotateKeyResponseDto>(s_jsonOptions);
        body.Should().NotBeNull();
        body!.KeyId.Should().NotBeNullOrEmpty();
        body.ExpiresAt.Should().BeAfter(body.ActivatedAt);

        // Audit entry written under the canonical action name on the host tenant
        // (PlatformAdmin actions land on the actor's host tenant, not the request body).
        using var scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var hits = await audit.SearchAsync(
            new TenantId(PlatformAdminApiFactory.HostTenantId),
            new AuditQuery(Action: "security.jwt.key_rotated", Page: 1, PageSize: 50),
            CancellationToken.None);
        hits.Items.Should().Contain(e =>
            e.TargetType == "jwt-key" &&
            e.TargetId == body.KeyId);
    }

    [Fact]
    public async Task ListKeys_ShouldReturn200_AndIncludeRotatedKey_WhenAuthorized()
    {
        // Ensure at least one rotation has run (idempotent — independent of test order).
        var rotation = await _client.PostAsync(
            "/api/v1/management/security/jwt/rotate-key", content: null);
        rotation.EnsureSuccessStatusCode();
        var rotated = await rotation.Content.ReadFromJsonAsync<RotateKeyResponseDto>(s_jsonOptions);
        rotated.Should().NotBeNull();

        var response = await _client.GetAsync("/api/v1/management/security/jwt/keys");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var listing = await response.Content.ReadFromJsonAsync<JwtKeyListResponseDto>(s_jsonOptions);
        listing.Should().NotBeNull();
        listing!.Keys.Should().Contain(k => k.KeyId == rotated!.KeyId);
        listing.Keys.Should().Contain(k => k.IsActive);
    }

    private sealed record RotateKeyResponseDto(
        string KeyId, DateTimeOffset ActivatedAt, DateTimeOffset ExpiresAt);

    private sealed record JwtKeyListResponseDto(IReadOnlyList<JwtKeyDtoTest> Keys);

    private sealed record JwtKeyDtoTest(
        string KeyId, DateTimeOffset ActivatedAt, DateTimeOffset ExpiresAt, bool IsActive);
}
