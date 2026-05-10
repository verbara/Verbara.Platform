using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Verbara.Platform.Api.Tests.Endpoints.AuthAdmin;

/// <summary>
/// Regression suite for the P0 finding <c>PREPUB-2026-05-09-ADMIN-001</c>:
/// the OIDC client secret on <c>tenant_auth_config</c> was returned in the
/// clear from <c>GET /admin/auth/config</c> (and echoed back on
/// <c>PUT /admin/auth/config</c>). After the fix, every HTTP response
/// projects to <c>TenantAuthConfigResponse</c> which exposes
/// <c>oidcClientSecretSet: bool</c> + <c>oidcClientSecretFingerprint: string</c>
/// instead of the raw value.
/// </summary>
public sealed class OidcClientSecretRedactionTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _authClient;

    public OidcClientSecretRedactionTests(AuthenticatedPlatformApiFactory factory)
    {
        _authClient = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GetConfig_ShouldReturnFingerprintOnly_WhenOidcClientSecretSet()
    {
        // Arrange — write a config with a known secret via the same surface
        // an operator would (PUT /admin/auth/config).
        const string rawSecret = "very-secret-do-not-leak-1234567890";
        var put = await _authClient.PutAsJsonAsync("/api/admin/auth/config", new
        {
            oidcEnabled = true,
            oidcAuthority = "https://idp.test",
            oidcClientId = "test-client",
            oidcClientSecret = rawSecret,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var response = await _authClient.GetAsync("/api/admin/auth/config");

        // Assert — the wire body must carry the redacted projection only.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(
            rawSecret,
            because: "ADMIN-001: the raw OIDC client secret must NEVER be returned over HTTP");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.TryGetProperty("oidcClientSecret", out _).Should().BeFalse(
            because: "the response DTO must omit the raw secret entirely, not just empty it");
        root.GetProperty("oidcClientSecretSet").GetBoolean().Should().BeTrue();
        root.GetProperty("oidcClientSecretFingerprint").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("oidcClientSecretFingerprint").GetString()!.Should().HaveLength(8);
    }

    [Fact]
    public async Task GetConfig_ShouldReturnFalse_WhenOidcClientSecretAbsent()
    {
        // Arrange — explicitly clear the OIDC secret regardless of what
        // sibling tests in this class may have written (xUnit does not
        // guarantee execution order across [Fact] methods sharing a fixture).
        // The PUT handler treats `null` as "leave field as-is" but accepts
        // an empty string to clear it; the OidcClientSecret setter on
        // TenantAuthConfig stores empty as "" and FromConfig treats both
        // null and empty as "absent".
        var clear = await _authClient.PutAsJsonAsync("/api/admin/auth/config", new
        {
            oidcClientSecret = "",
        });
        clear.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var response = await _authClient.GetAsync("/api/admin/auth/config");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Either the property is omitted (CamelCase + DefaultIgnoreCondition.WhenWritingNull)
        // or present with the documented "not set" shape.
        root.GetProperty("oidcClientSecretSet").GetBoolean().Should().BeFalse(
            because: "when no secret is configured the response must explicitly say so");
        var hasFingerprint = root.TryGetProperty("oidcClientSecretFingerprint", out var fpProp);
        if (hasFingerprint)
            fpProp.ValueKind.Should().Be(JsonValueKind.Null,
                because: "fingerprint must be null when no secret is configured");
    }

    [Fact]
    public async Task PutConfig_ShouldReturnFingerprintOnly_WhenOidcClientSecretProvided()
    {
        const string rawSecret = "another-secret-also-keep-private";
        var response = await _authClient.PutAsJsonAsync("/api/admin/auth/config", new
        {
            oidcEnabled = true,
            oidcAuthority = "https://idp.test",
            oidcClientId = "test-client-2",
            oidcClientSecret = rawSecret,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(
            rawSecret,
            because: "ADMIN-001: PUT must not echo the secret back in its response either");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("oidcClientSecretSet").GetBoolean().Should().BeTrue();
        root.GetProperty("oidcClientSecretFingerprint").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetConfig_FingerprintShouldChange_WhenSecretRotates()
    {
        // Operator-visible signal that a rotation actually happened: the
        // 8-hex-char fingerprint flips. This is the documented Scope 5.4
        // "fingerprint-only" pattern.
        var put1 = await _authClient.PutAsJsonAsync("/api/admin/auth/config", new
        {
            oidcEnabled = true,
            oidcAuthority = "https://idp.test",
            oidcClientId = "rotating-client",
            oidcClientSecret = "secret-version-A-aaaaaaaaaaaaaaaaa",
        });
        put1.StatusCode.Should().Be(HttpStatusCode.OK);
        var fingerprintA = await ReadFingerprintAsync();

        var put2 = await _authClient.PutAsJsonAsync("/api/admin/auth/config", new
        {
            oidcClientSecret = "secret-version-B-bbbbbbbbbbbbbbbbb",
        });
        put2.StatusCode.Should().Be(HttpStatusCode.OK);
        var fingerprintB = await ReadFingerprintAsync();

        fingerprintA.Should().NotBeNullOrEmpty();
        fingerprintB.Should().NotBeNullOrEmpty();
        fingerprintB.Should().NotBe(
            fingerprintA,
            because: "rotating the OIDC client secret must surface as a fingerprint change for operator visibility");
    }

    private async Task<string?> ReadFingerprintAsync()
    {
        var response = await _authClient.GetAsync("/api/admin/auth/config");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("oidcClientSecretFingerprint").GetString();
    }
}
