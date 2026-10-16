using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// Tenant status is enforced on every sign-in path, wherever the request names its tenant: the login body,
/// the API key, the MFA challenge and the refresh cookie. None of these requests carries <c>X-Tenant-Id</c> or a
/// tenant subdomain, so the answer comes from the sign-in handler itself. The internal user-access decision
/// Realtime consults on every hub connection covers tenant status too.
/// </summary>
/// <remarks>
/// Each test names its own tenant in the host's in-memory <see cref="ITenantStore"/>, so no test sees a status
/// another one set.
/// </remarks>
public sealed class TenantStatusSignInPathTests : IClassFixture<AccountStatusApiFactory>
{
    private const string TenantSuspendedCode = "tenant-suspended";
    private const string TenantPendingDeletionCode = "tenant-pending-deletion";
    private const string Password = "Tenant-Status-Passw0rd!";
    private const string MfaSecret = "JBSWY3DPEHPK3PXP";

    private readonly AccountStatusApiFactory _factory;

    public TenantStatusSignInPathTests(AccountStatusApiFactory factory) => _factory = factory;

    public static TheoryData<TenantStatus> RefusedStatuses => new()
    {
        TenantStatus.Suspended,
        TenantStatus.PendingDeletion,
    };

    public static TheoryData<TenantStatus> ServingStatuses => new()
    {
        TenantStatus.Active,
        TenantStatus.Warning,
    };

    // ─── POST /auth/login, tenant in the body ────────────────────────────────

    [Theory]
    [MemberData(nameof(RefusedStatuses))]
    public async Task Login_ShouldAnswerTenantStatusCodeWithoutTokens_WhenBodyTenantIsNotServing(TenantStatus status)
    {
        var tenant = NewTenant(status);
        var user = NewUser(tenant);
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { tenantId = tenant, email = user.Email, password = Password });

        await AssertSoleErrorAsync(response, HttpStatusCode.Forbidden, CodeFor(status));
        AssertNoRefreshCookie(response);
    }

    [Theory]
    [MemberData(nameof(RefusedStatuses))]
    public async Task Login_ShouldAnswerTenantStatusCodeWithoutCountingAFailedAttempt_WhenBodyTenantIsNotServingAndPasswordIsWrong(
        TenantStatus status)
    {
        var tenant = NewTenant(status);
        var user = NewUser(tenant);
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { tenantId = tenant, email = user.Email, password = "Wrong-" + Password });

        await AssertSoleErrorAsync(response, HttpStatusCode.Forbidden, CodeFor(status));
        _factory.GetUser(user.UserId.Value, tenant)!.FailedLoginAttempts.Should().Be(0,
            because: "the tenant status is checked before the credentials, so a wrong password is never counted");
    }

    [Theory]
    [MemberData(nameof(RefusedStatuses))]
    public async Task Login_ShouldAnswerTenantStatusCode_WhenBodyTenantIsNotServingAndEmailIsUnknown(TenantStatus status)
    {
        var tenant = NewTenant(status);
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { tenantId = tenant, email = $"{NewId("nobody")}@tenant-status.test", password = Password });

        await AssertSoleErrorAsync(response, HttpStatusCode.Forbidden, CodeFor(status));
    }

    [Fact]
    public async Task Login_ShouldAnswer404_WhenBodyTenantIsDeleted()
    {
        var tenant = NewTenant(TenantStatus.Deleted);
        var user = NewUser(tenant);
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { tenantId = tenant, email = user.Email, password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "a Deleted tenant answers 404 on the sign-in endpoints wherever it is named");
        AssertNoRefreshCookie(response);
    }

    [Theory]
    [MemberData(nameof(ServingStatuses))]
    public async Task Login_ShouldIssueTokens_WhenBodyTenantIsServing(TenantStatus status)
    {
        var tenant = NewTenant(status);
        var user = NewUser(tenant);
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { tenantId = tenant, email = user.Email, password = Password });

        await AssertTokensIssuedAsync(response);
    }

    // ─── POST /auth/mfa/verify, tenant in the challenge ──────────────────────

    [Theory]
    [MemberData(nameof(RefusedStatuses))]
    public async Task MfaVerify_ShouldAnswerTenantStatusCodeWithoutTokens_WhenChallengeTenantIsNotServing(TenantStatus status)
    {
        var tenant = NewTenant(TenantStatus.Active);
        var challenge = await MintChallengeAsync(SeedMfaUser(tenant));
        SetTenantStatus(tenant, status);
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/mfa/verify",
            new { mfaToken = challenge, code = CorrectTotp() });

        await AssertSoleErrorAsync(response, HttpStatusCode.Forbidden, CodeFor(status));
        AssertNoRefreshCookie(response);
    }

    [Theory]
    [MemberData(nameof(ServingStatuses))]
    public async Task MfaVerify_ShouldIssueTokens_WhenChallengeTenantIsServing(TenantStatus status)
    {
        var tenant = NewTenant(status);
        var challenge = await MintChallengeAsync(SeedMfaUser(tenant));
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/mfa/verify",
            new { mfaToken = challenge, code = CorrectTotp() });

        await AssertTokensIssuedAsync(response);
    }

    // ─── POST /auth/login/apikey, tenant of the key ──────────────────────────

    [Theory]
    [MemberData(nameof(RefusedStatuses))]
    public async Task ApiKeyLogin_ShouldAnswerTenantStatusCodeWithoutToken_WhenKeyTenantIsNotServing(TenantStatus status)
    {
        var tenant = NewTenant(status);
        var rawKey = await SeedApiKeyAsync(NewUser(tenant));
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/login/apikey", new { apiKey = rawKey });

        await AssertSoleErrorAsync(response, HttpStatusCode.Forbidden, CodeFor(status));
    }

    [Theory]
    [MemberData(nameof(ServingStatuses))]
    public async Task ApiKeyLogin_ShouldIssueToken_WhenKeyTenantIsServing(TenantStatus status)
    {
        var tenant = NewTenant(status);
        var rawKey = await SeedApiKeyAsync(NewUser(tenant));
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/login/apikey", new { apiKey = rawKey });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await AccessTokenOfAsync(response)).Should().NotBeNullOrEmpty();
    }

    // ─── POST /auth/refresh, tenant of the stored token ──────────────────────

    [Theory]
    [MemberData(nameof(RefusedStatuses))]
    public async Task Refresh_ShouldAnswerBodyless401AndRevokeTheLineage_WhenTokenTenantIsNotServing(TenantStatus status)
    {
        var tenant = NewTenant(TenantStatus.Active);
        var user = NewUser(tenant);
        var rawToken = await SeedRefreshTokenAsync(user);
        await SeedRefreshTokenAsync(user);
        SetTenantStatus(tenant, status);
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(RefreshRequest(rawToken));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "a refresh token must stop minting access tokens once its tenant stops serving");
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty(
            because: "the refresh refusal adds no new body");
        (await ActiveRefreshTokensAsync(user)).Should().BeEmpty(
            because: "the whole refresh lineage of the user ends, the replacement minted by the rotation included");
        string.Join(";", response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [])
            .Should().Contain(RefreshTokenCookie.Name + "=;",
                because: "the dead cookie is cleared so the browser stops replaying it");
    }

    [Theory]
    [MemberData(nameof(ServingStatuses))]
    public async Task Refresh_ShouldIssueTokens_WhenTokenTenantIsServing(TenantStatus status)
    {
        var tenant = NewTenant(status);
        var rawToken = await SeedRefreshTokenAsync(NewUser(tenant));
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(RefreshRequest(rawToken));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await AccessTokenOfAsync(response)).Should().NotBeNullOrEmpty();
    }

    // ─── GET /api/v1/internal/user-access, the Realtime decision ─────────────

    [Theory]
    [InlineData(TenantStatus.Suspended, false)]
    [InlineData(TenantStatus.PendingDeletion, false)]
    [InlineData(TenantStatus.Deleted, false)]
    [InlineData(TenantStatus.Active, true)]
    [InlineData(TenantStatus.Warning, true)]
    public async Task GetUserAccess_ShouldReportAllowedOnlyForServingTenant_WhenUserIsActive(
        TenantStatus status, bool expectedAllowed)
    {
        var tenant = NewTenant(status);
        var user = NewUser(tenant);
        using var client = _factory.CreateServiceClient();

        using var response = await client.GetAsync($"/api/v1/internal/user-access/{tenant}/{user.UserId.Value}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAllowedAsync(response)).Should().Be(expectedAllowed,
            because: "the decision Realtime consults covers the tenant's status as well as the account's");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..12]}";

    private static string CodeFor(TenantStatus status) =>
        status == TenantStatus.Suspended ? TenantSuspendedCode : TenantPendingDeletionCode;

    private string NewTenant(TenantStatus status)
    {
        var tenantId = NewId("tssp");
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantStore>().UpsertAsync(new Tenant
        {
            TenantId = tenantId,
            Name = tenantId,
            Status = status,
            Type = TenantType.Customer,
            ParentTenantId = AccountStatusApiFactory.PlatformTenantId,
        }).AsTask().GetAwaiter().GetResult();
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(_factory.Services, tenantId);
        return tenantId;
    }

    private void SetTenantStatus(string tenantId, TenantStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var tenant = store.GetAsync(tenantId, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        tenant.Should().NotBeNull();
        tenant!.Status = status;
        store.UpsertAsync(tenant).AsTask().GetAwaiter().GetResult();
    }

    private User NewUser(string tenantId) =>
        _factory.SaveUser(NewId("tssp-user"), tenantId, UserRole.Agent, UserStatus.Active, Password);

    private User SeedMfaUser(string tenantId)
    {
        var userId = NewId("tssp-mfa");
        return _factory.SeedUser(new User
        {
            UserId = EntityId.From(userId),
            TenantId = new TenantId(tenantId),
            Email = $"{userId}@tenant-status.test",
            DisplayName = userId,
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            MfaEnabled = true,
            MfaSecret = MfaSecret,
            MfaConfirmedAt = DateTimeOffset.UtcNow,
        });
    }

    private async Task<string> MintChallengeAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        return await AuthEndpoints.GenerateMfaChallengeTokenAndStoreAsync(
            user.UserId.Value, user.TenantId.Value,
            scope.ServiceProvider.GetRequiredService<IMfaPendingCache>(), CancellationToken.None);
    }

    private async Task<string> SeedApiKeyAsync(User owner)
    {
        var rawKey = NewId("tssp-key");
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IApiKeyStore>().SaveAsync(new ApiKey
        {
            KeyId = EntityId.From(NewId("key")),
            TenantId = owner.TenantId,
            Name = "tenant-status-key",
            HashedKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))),
            Scopes = ["*"],
            UserId = owner.UserId,
            KeyType = ApiKeyType.Standard,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        return rawKey;
    }

    private async Task<string> SeedRefreshTokenAsync(User user)
    {
        var (rawToken, _) = await _factory.Services.GetRequiredService<RefreshTokenService>()
            .GenerateAsync(user.UserId.Value, user.TenantId.Value, "10.0.0.1", "test", CancellationToken.None);
        return rawToken;
    }

    private Task<IReadOnlyList<RefreshToken>> ActiveRefreshTokensAsync(User user) =>
        _factory.Services.GetRequiredService<IRefreshTokenStore>()
            .GetActiveByUserAsync(user.TenantId.Value, user.UserId.Value, CancellationToken.None);

    private static HttpRequestMessage RefreshRequest(string rawToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Cookie", $"{RefreshTokenCookie.Name}={rawToken}");
        return request;
    }

    private static async Task AssertSoleErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(["error"],
            because: "the body carries the error member and nothing else");
        doc.RootElement.GetProperty("error").GetString().Should().Be(error);
    }

    private static async Task AssertTokensIssuedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await AccessTokenOfAsync(response)).Should().NotBeNullOrEmpty();
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(c => c.StartsWith(RefreshTokenCookie.Name + "=", StringComparison.Ordinal)
            && !c.StartsWith(RefreshTokenCookie.Name + "=;", StringComparison.Ordinal));
    }

    private static void AssertNoRefreshCookie(HttpResponseMessage response)
    {
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        cookies.Should().NotContain(c => c.StartsWith(RefreshTokenCookie.Name + "=", StringComparison.Ordinal)
                && !c.StartsWith(RefreshTokenCookie.Name + "=;", StringComparison.Ordinal),
            because: "no refresh token may be handed out");
    }

    private static async Task<string?> AccessTokenOfAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("accessToken", out var token) ? token.GetString() : null;
    }

    private static async Task<bool> ReadAllowedAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "allowed", StringComparison.OrdinalIgnoreCase))
                return property.Value.GetBoolean();
        }

        throw new InvalidOperationException("The user-access response carries no 'allowed' field.");
    }

    private static string CorrectTotp() => new Totp(Base32Encoding.ToBytes(MfaSecret)).ComputeTotp(DateTime.UtcNow);
}
