using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// surface-agent-presence-admin-controls (host / producer): asserts the
/// pure-surfacing round-trip of the already-persisted
/// <c>TenantAuthConfig.PendingPauseTimeoutMinutes</c> on the tenant
/// auth-config HTTP contract — <c>PUT</c> persists it and both the
/// <c>PUT</c>-after-write echo and <c>GET</c> emit it, under the verbatim
/// wire name <c>pendingPauseTimeoutMinutes</c> pinned to the change fixtures.
/// </summary>
public sealed class PendingPauseTimeoutSurfaceTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private const string ConfigRoute = "/api/v1/admin/auth/config";
    private const string WireField = "pendingPauseTimeoutMinutes";

    private readonly HttpClient _authClient;

    public PendingPauseTimeoutSurfaceTests(AuthenticatedPlatformApiFactory factory)
    {
        _authClient = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task UpdateConfig_ShouldPersistAndEchoPendingPauseTimeout_WhenFieldProvided()
    {
        // Fixture body: { "pendingPauseTimeoutMinutes": 20 }
        var putResponse = await _authClient.PutAsJsonAsync(ConfigRoute, new { pendingPauseTimeoutMinutes = 20 });
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // PUT-after-write echoes the newly persisted value.
        (await ReadPendingPauseTimeout(putResponse)).Should().Be(20);

        // GET echoes the persisted value across a separate request.
        var getResponse = await _authClient.GetAsync(ConfigRoute);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadPendingPauseTimeout(getResponse)).Should().Be(20);
    }

    [Fact]
    public async Task UpdateConfig_ShouldAcceptZeroToDisableTimeout_WhenFieldIsZero()
    {
        // 0 (or less) disables the timeout for the tenant — the model field's
        // documented semantics; the surfacing must not reject it.
        var putResponse = await _authClient.PutAsJsonAsync(ConfigRoute, new { pendingPauseTimeoutMinutes = 0 });
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadPendingPauseTimeout(putResponse)).Should().Be(0);

        var getResponse = await _authClient.GetAsync(ConfigRoute);
        (await ReadPendingPauseTimeout(getResponse)).Should().Be(0);
    }

    [Fact]
    public async Task UpdateConfig_ShouldLeavePendingPauseTimeoutUntouched_WhenFieldOmitted()
    {
        // Establish a known persisted value.
        var seed = await _authClient.PutAsJsonAsync(ConfigRoute, new { pendingPauseTimeoutMinutes = 45 });
        seed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadPendingPauseTimeout(seed)).Should().Be(45);

        // A partial update that omits pendingPauseTimeoutMinutes must not overwrite it.
        var partial = await _authClient.PutAsJsonAsync(ConfigRoute, new { passwordMinLength = 14 });
        partial.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadPendingPauseTimeout(partial)).Should().Be(45);

        var getResponse = await _authClient.GetAsync(ConfigRoute);
        (await ReadPendingPauseTimeout(getResponse)).Should().Be(45);
    }

    [Fact]
    public async Task GetConfig_ShouldEmitPendingPauseTimeoutUnderVerbatimWireName_AndNeverLeakOidcSecret()
    {
        await _authClient.PutAsJsonAsync(ConfigRoute, new { pendingPauseTimeoutMinutes = 30 });

        var getResponse = await _authClient.GetAsync(ConfigRoute);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await getResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Wire name pinned verbatim to fixtures/tenant-auth-config-response.json.
        root.TryGetProperty(WireField, out var field).Should().BeTrue(
            because: "the response must emit the field under the exact camelCase name the Web child binds to");
        field.GetInt32().Should().Be(30);

        // Redaction seam intact — the raw OIDC secret is never emitted (PREPUB-2026-05-09-ADMIN-001).
        root.TryGetProperty("oidcClientSecret", out _).Should().BeFalse();
        root.TryGetProperty("oidcClientSecretSet", out _).Should().BeTrue();
    }

    private static async Task<int> ReadPendingPauseTimeout(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty(WireField, out var field).Should().BeTrue(
            because: "the response must carry the surfaced pendingPauseTimeoutMinutes field");
        return field.GetInt32();
    }
}
