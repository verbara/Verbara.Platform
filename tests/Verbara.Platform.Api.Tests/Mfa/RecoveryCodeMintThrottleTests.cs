using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Xunit;

namespace Verbara.Platform.Api.Tests.Mfa;

/// <summary>
/// The per-user recovery-code mint allowance (openspec <c>harden-recovery-code-digest</c>, design D10).
/// A mint is ten Argon2id derivations (about 0.4 s of one core and about 1 GB allocated, RC0), and all four
/// mint endpoints take only the caller's own credentials, so without a bound one access token could keep the
/// box's whole Argon2id capacity busy. One mint per user per interval, shared by the four paths, and a refusal
/// is a 429 with <c>Retry-After</c> before any derivation runs.
/// </summary>
/// <remarks>
/// Each test builds its own host (<see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>) so the
/// allowance starts empty, and sets the interval explicitly rather than relying on the default.
/// </remarks>
public sealed class RecoveryCodeMintThrottleTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private const string Password = "CurrentPassword123!";
    private const string StepUpSecret = "JBSWY3DPEHPK3PXP";

    private readonly AuthenticatedPlatformApiFactory _factory;

    public RecoveryCodeMintThrottleTests(AuthenticatedPlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task MfaSetup_ShouldReturn429WithRetryAfterAndKeepCodes_WhenSameUserMintsAgainWithinInterval()
    {
        await using var host = WithMintInterval("00:01:00");
        var client = host.CreateAuthenticatedClient();
        var user = await ResetUserAsync(host);

        var first = await client.PostAsync("/api/v1/auth/mfa/setup", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var storedAfterFirst = user.MfaRecoveryCodes!.ToArray();

        var second = await client.PostAsync("/api/v1/auth/mfa/setup", null);

        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            because: "a second mint for the same user inside the interval must be refused before it derives anything");
        second.Headers.RetryAfter.Should().NotBeNull();
        second.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
        user.MfaRecoveryCodes.Should().Equal(storedAfterFirst,
            because: "a refused mint writes nothing: the codes the first mint returned stay valid");
    }

    [Fact]
    public async Task ProfileEnrollVerify_ShouldReturn429_WhenSameUserMintedThroughAnotherPathWithinInterval()
    {
        await using var host = WithMintInterval("00:01:00");
        var client = host.CreateAuthenticatedClient();
        await ResetUserAsync(host);

        (await client.PostAsync("/api/v1/auth/mfa/setup", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var init = await client.PostAsync("/api/v1/profile/security/mfa/enroll/init", null);
        init.StatusCode.Should().Be(HttpStatusCode.OK);
        using var initDoc = JsonDocument.Parse(await init.Content.ReadAsStringAsync());
        var secret = initDoc.RootElement.GetProperty("secret").GetString()!;

        var verify = await client.PostAsJsonAsync(
            "/api/v1/profile/security/mfa/enroll/verify", new { secret, totpCode = ComputeTotp(secret) });

        verify.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            because: "the allowance is per user across all four mint paths, not per endpoint");
        RetryAfterSecondsOf(verify).Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task RegenerateRecoveryCodes_ShouldReturn429WithRetryAfterAndKeepCodes_WhenSameUserMintedWithinInterval()
    {
        await using var host = WithMintInterval("00:01:00");
        var client = host.CreateAuthenticatedClient();
        var user = await MintThenEnableAsync(host, client);
        var storedBefore = user.MfaRecoveryCodes!.ToArray();

        var regenerate = await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/recovery-codes/regenerate", new { password = Password });

        regenerate.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            because: "the allowance is per user across all four mint paths, not per endpoint");
        RetryAfterSecondsOf(regenerate).Should().BeGreaterThanOrEqualTo(1);
        user.MfaRecoveryCodes.Should().Equal(storedBefore, because: "a refused mint writes nothing");
    }

    [Fact]
    public async Task ProfileRegenerateRecoveryCodes_ShouldReturn429WithRetryAfterAndKeepCodes_WhenSameUserMintedWithinInterval()
    {
        await using var host = WithMintInterval("00:01:00");
        var client = host.CreateAuthenticatedClient();
        var user = await MintThenEnableAsync(host, client);
        var storedBefore = user.MfaRecoveryCodes!.ToArray();

        var regenerate = await client.PostAsJsonAsync(
            "/api/v1/profile/security/recovery-codes/regenerate", new { totpCode = ComputeTotp(StepUpSecret) });

        regenerate.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            because: "the allowance is per user across all four mint paths, not per endpoint");
        RetryAfterSecondsOf(regenerate).Should().BeGreaterThanOrEqualTo(1);
        user.MfaRecoveryCodes.Should().Equal(storedBefore, because: "a refused mint writes nothing");
    }

    [Fact]
    public async Task RegenerateRecoveryCodes_ShouldNotSpendMintAllowance_WhenPasswordIsWrong()
    {
        await using var host = WithMintInterval("00:01:00");
        var client = host.CreateAuthenticatedClient();
        var user = await ResetUserAsync(host);
        user.MfaEnabled = true;
        user.PasswordHash = PasswordService.HashPassword(Password);

        var wrong = await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/recovery-codes/regenerate", new { password = "not-the-password" });
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var right = await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/recovery-codes/regenerate", new { password = Password });

        right.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the allowance is spent by a mint, not by a refused credential check");
    }

    /// <summary>
    /// Spends the user's allowance with one mint through <c>/auth/mfa/setup</c>, then leaves the account with
    /// MFA on, a known authenticator secret and a known password, so either regenerate route passes its own
    /// credential checks and reaches the allowance.
    /// </summary>
    private static async Task<User> MintThenEnableAsync(AuthenticatedHost host, HttpClient client)
    {
        var user = await ResetUserAsync(host);
        (await client.PostAsync("/api/v1/auth/mfa/setup", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        user.MfaRecoveryCodes.Should().NotBeNullOrEmpty(because: "the first mint stored codes");
        user.MfaEnabled = true;
        user.MfaSecret = StepUpSecret;
        user.MfaConfirmedAt = DateTimeOffset.UtcNow;
        user.PasswordHash = PasswordService.HashPassword(Password);
        return user;
    }

    /// <summary>The <c>Retry-After</c> header as whole seconds; fails if it is absent or not an integer.</summary>
    private static int RetryAfterSecondsOf(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Retry-After", out var values).Should().BeTrue(
            because: "a throttled mint says how long to wait");
        var raw = values!.Single();
        int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var seconds).Should().BeTrue(because: $"Retry-After is whole seconds, got '{raw}'");
        return seconds;
    }

    private AuthenticatedHost WithMintInterval(string interval) =>
        new(_factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mfa:RecoveryCodeMint:MinInterval"] = interval,
            }))));

    private static async Task<User> ResetUserAsync(AuthenticatedHost host)
    {
        var user = await host.Services.GetRequiredService<IUserStore>().GetByIdAsync(
            new TenantId(AuthenticatedPlatformApiFactory.TestTenantId),
            EntityId.From(AuthenticatedPlatformApiFactory.TestUserId),
            CancellationToken.None);
        user.Should().NotBeNull();
        user!.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaRecoveryCodes = null;
        user.MfaConfirmedAt = null;
        user.PasswordHash = null;
        return user;
    }

    private static string ComputeTotp(string secret) =>
        new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(DateTime.UtcNow);

    /// <summary>A derived host plus the authenticated client the base factory knows how to build.</summary>
    private sealed class AuthenticatedHost(WebApplicationFactory<Program> inner) : IAsyncDisposable
    {
        public IServiceProvider Services => inner.Services;

        public HttpClient CreateAuthenticatedClient()
        {
            var client = inner.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
            client.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);
            return client;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
