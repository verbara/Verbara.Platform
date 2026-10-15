using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Users;

/// <summary>
/// R5.2 E.2 — verify <c>/users/me</c> returns the new <c>mfaPolicy</c> snapshot
/// (Enforced + PolicySource) so the Web can hide "Disable MFA" proactively
/// instead of relying on a 403 round-trip from the existing policy enforcer.
/// Pairs with the same gating wired in <c>MfaPolicyEnforcementTests</c>.
/// </summary>
public sealed class UsersMeMfaPolicyTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public UsersMeMfaPolicyTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GetUsersMe_ShouldIncludeMfaPolicy_WhenEnforced()
    {
        // Seed a tenant policy that requires MFA for all roles. The factory
        // pre-seeds an Admin user under the test tenant.
        using var scope = _factory.Services.CreateScope();
        var configStore = scope.ServiceProvider.GetRequiredService<ITenantAuthConfigStore>();
        await configStore.SaveAsync(new TenantAuthConfig
        {
            TenantId = AuthenticatedPlatformApiFactory.TestTenantId,
            MfaPolicy = "required_all",
        }, CancellationToken.None);

        var response = await _client.GetAsync("/api/v1/users/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<MeDto>(s_jsonOptions);
        dto.Should().NotBeNull();
        dto!.MfaPolicy.Should().NotBeNull();
        dto.MfaPolicy!.Enforced.Should().BeTrue();
        dto.MfaPolicy.PolicySource.Should().Be("tenant");
    }

    [Fact]
    public async Task GetUsersMe_ShouldIncludeMfaPolicy_WhenNotEnforced()
    {
        using var scope = _factory.Services.CreateScope();
        var configStore = scope.ServiceProvider.GetRequiredService<ITenantAuthConfigStore>();
        await configStore.SaveAsync(new TenantAuthConfig
        {
            TenantId = AuthenticatedPlatformApiFactory.TestTenantId,
            MfaPolicy = "optional",
        }, CancellationToken.None);

        var response = await _client.GetAsync("/api/v1/users/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<MeDto>(s_jsonOptions);
        dto.Should().NotBeNull();
        dto!.MfaPolicy.Should().NotBeNull();
        dto.MfaPolicy!.Enforced.Should().BeFalse();
        dto.MfaPolicy.PolicySource.Should().Be("user");
    }

    // ─── harden-recovery-code-digest 1.7 — recoveryCodesNeedRegeneration ───
    //
    // A derived flag (design D7): true while any stored element is in a legacy family
    // (BCrypt or salted SHA-256), false once every element is Argon2id, false with no codes.
    // Classification is prefix-only (design D5), so the fixtures are shape-correct literals,
    // not real digests.

    private const string Argon2idShaped = "$argon2id$v=19$m=12288,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGFzaGhhc2g";
    private const string BcryptShaped = "$2a$10$abcdefghijklmnopqrstuuJ0123456789abcdefghijklmnopqrstuv";
    private const string Sha256Shaped = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

    [Fact]
    public async Task GetMe_ShouldReportRecoveryCodesNeedRegeneration_WhenAnyStoredElementIsLegacy()
    {
        var flag = await GetRecoveryCodesNeedRegenerationAsync([Argon2idShaped, BcryptShaped, Sha256Shaped]);

        flag.Should().BeTrue(
            because: "one legacy element is enough: the user should regenerate to reach Argon2id");
    }

    [Fact]
    public async Task GetMe_ShouldNotReportRecoveryCodesNeedRegeneration_WhenAllElementsAreArgon2id()
    {
        var flag = await GetRecoveryCodesNeedRegenerationAsync([Argon2idShaped, Argon2idShaped]);

        flag.Should().BeFalse(
            because: "an all-Argon2id array is what a regenerate produces, so the prompt clears");
    }

    [Fact]
    public async Task GetMe_ShouldNotReportRecoveryCodesNeedRegeneration_WhenUserHasNoCodes()
    {
        var flag = await GetRecoveryCodesNeedRegenerationAsync(null);

        flag.Should().BeFalse(
            because: "a user with no recovery codes has nothing to regenerate");
    }

    [Fact]
    public async Task GetMe_ShouldNotReportRecoveryCodesNeedRegeneration_WhenMfaIsNotEnabled()
    {
        // Codes left behind by an abandoned enrollment (setup or wizard verify, never confirmed): neither
        // regenerate endpoint accepts a user whose MFA is off, so prompting would point at a dead end.
        var flag = await GetRecoveryCodesNeedRegenerationAsync([BcryptShaped, Sha256Shaped], mfaEnabled: false);

        flag.Should().BeFalse(
            because: "pending codes are never redeemable and cannot be regenerated, so there is nothing to prompt for");
    }

    [Fact]
    public async Task GetMe_ShouldClearRecoveryCodesNeedRegeneration_WhenUserRegeneratesThroughTheEndpoint()
    {
        // Spec scenario "Regenerating clears the prompt", end to end: legacy array → real regenerate → flag false.
        const string password = "CurrentPassword123!";
        var user = await GetSeededUserAsync();
        var (previousCodes, previousEnabled, previousHash) = (user.MfaRecoveryCodes, user.MfaEnabled, user.PasswordHash);
        user.MfaRecoveryCodes = [BcryptShaped, Sha256Shaped];
        user.MfaEnabled = true;
        user.PasswordHash = Verbara.Platform.Api.Services.PasswordService.HashPassword(password);
        try
        {
            (await ReadRecoveryCodesNeedRegenerationAsync()).Should().BeTrue(
                because: "a legacy array must raise the prompt before the regenerate");

            var regenerate = await _client.PostAsJsonAsync(
                "/api/v1/auth/mfa/recovery-codes/regenerate", new { password });
            regenerate.StatusCode.Should().Be(HttpStatusCode.OK);

            (await ReadRecoveryCodesNeedRegenerationAsync()).Should().BeFalse(
                because: "every code a regenerate mints is Argon2id, so the prompt clears");
        }
        finally
        {
            user.MfaRecoveryCodes = previousCodes;
            user.MfaEnabled = previousEnabled;
            user.PasswordHash = previousHash;
        }
    }

    /// <summary>
    /// Sets the seeded user's stored digests (and whether MFA is on), reads <c>GET /users/me</c> and returns
    /// the <c>recoveryCodesNeedRegeneration</c> property, failing if it is absent. Both are restored afterwards
    /// because the factory hands out one shared user instance.
    /// </summary>
    private async Task<bool> GetRecoveryCodesNeedRegenerationAsync(string[]? stored, bool mfaEnabled = true)
    {
        var user = await GetSeededUserAsync();

        var (previous, previousEnabled) = (user.MfaRecoveryCodes, user.MfaEnabled);
        user.MfaRecoveryCodes = stored;
        user.MfaEnabled = mfaEnabled;
        try
        {
            return await ReadRecoveryCodesNeedRegenerationAsync();
        }
        finally
        {
            user.MfaRecoveryCodes = previous;
            user.MfaEnabled = previousEnabled;
        }
    }

    private async Task<bool> ReadRecoveryCodesNeedRegenerationAsync()
    {
        var response = await _client.GetAsync("/api/v1/users/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("recoveryCodesNeedRegeneration", out var flag).Should().BeTrue(
            because: "GET /users/me must expose recoveryCodesNeedRegeneration for the console banner (D7)");
        return flag.GetBoolean();
    }

    private async Task<User> GetSeededUserAsync()
    {
        var user = await _factory.Services.GetRequiredService<IUserStore>().GetByIdAsync(
            new TenantId(AuthenticatedPlatformApiFactory.TestTenantId),
            EntityId.From(AuthenticatedPlatformApiFactory.TestUserId),
            CancellationToken.None);
        user.Should().NotBeNull();
        return user!;
    }

    private sealed record MeDto(string UserId, string Email, MfaPolicySnapshot? MfaPolicy);

    private sealed record MfaPolicySnapshot(bool Enforced, string PolicySource);
}
