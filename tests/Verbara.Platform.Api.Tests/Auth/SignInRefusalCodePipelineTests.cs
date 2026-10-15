using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// The tenant-status refusal of the three anonymous sign-in endpoints carries a machine code through the
/// real middleware pipeline (openspec <c>sign-in-refusals-say-what-happened</c>, spec
/// <c>sign-in-refusal-codes</c>), and every other tenant refusal keeps the body it has today.
/// </summary>
/// <remarks>
/// Each test names its own tenant in the host's in-memory <see cref="ITenantStore"/>, so no test sees a
/// status another one set.
/// </remarks>
public sealed partial class SignInRefusalCodePipelineTests : IClassFixture<AccountStatusApiFactory>
{
    private const string TenantSuspendedCode = "tenant-suspended";
    private const string TenantPendingDeletionCode = "tenant-pending-deletion";
    private const string ExhaustedCode = "mfa-challenge-exhausted";
    private const string MfaSecret = "JBSWY3DPEHPK3PXP";

    private readonly AccountStatusApiFactory _factory;

    public SignInRefusalCodePipelineTests(AccountStatusApiFactory factory) => _factory = factory;

    // ─── The sign-in endpoints answer the tenant code ────────────────────────

    [Fact]
    public async Task Login_ShouldAnswerTenantSuspendedCode_WhenNamedTenantIsSuspended()
    {
        var tenant = NewTenant(TenantStatus.Suspended);
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Post("/api/v1/auth/login", tenant,
            new { email = "anyone@tenant.test", password = "AnyPassword123!" }));

        await AssertSoleErrorAsync(response, HttpStatusCode.Forbidden, TenantSuspendedCode);
    }

    [Fact]
    public async Task ApiKeyLogin_ShouldAnswerTenantPendingDeletionCode_WhenNamedTenantIsPendingDeletion()
    {
        var tenant = NewTenant(TenantStatus.PendingDeletion);
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Post("/api/v1/auth/login/apikey", tenant,
            new { apiKey = "any-api-key" }));

        await AssertSoleErrorAsync(response, HttpStatusCode.Forbidden, TenantPendingDeletionCode);
    }

    [Fact]
    public async Task MfaVerify_ShouldAnswerTenantPendingDeletionCode_WhenNamedTenantIsPendingDeletion()
    {
        var tenant = NewTenant(TenantStatus.PendingDeletion);
        var user = SeedMfaUser(tenant);
        var challenge = await MintChallengeAsync(user);
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Post("/api/v1/auth/mfa/verify", tenant,
            new { mfaToken = challenge, code = CorrectTotp() }));

        await AssertSoleErrorAsync(response, HttpStatusCode.Forbidden, TenantPendingDeletionCode);
    }

    [Fact]
    public async Task MfaVerify_ShouldLeaveEveryAttemptOfTheChallenge_WhenTenantRefusalAnswersFirst()
    {
        // The tenant refusal is answered before the handler, so the challenge it names keeps all three
        // attempts: once the tenant is Active again, two wrong codes still answer the plain wrong-factor 401
        // and a correct third code signs in. Had the refused request spent an attempt, the second wrong
        // code would have spent the challenge (401 mfa-challenge-exhausted) and the correct one would be
        // refused as expired.
        var tenant = NewTenant(TenantStatus.PendingDeletion);
        var user = SeedMfaUser(tenant);
        var challenge = await MintChallengeAsync(user);
        using var client = _factory.CreateClient();

        using (var refused = await client.SendAsync(Post("/api/v1/auth/mfa/verify", tenant,
                   new { mfaToken = challenge, code = CorrectTotp() })))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                because: "the precondition is a request refused for its tenant");
        }

        SetTenantStatus(tenant, TenantStatus.Active);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var wrong = await client.SendAsync(Post("/api/v1/auth/mfa/verify", tenant,
                new { mfaToken = challenge, code = WrongTotp() }));
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ErrorOfAsync(wrong)).Should().NotBe(ExhaustedCode,
                because: $"wrong code #{attempt} is within the challenge's three attempts");
        }

        using var correct = await client.SendAsync(Post("/api/v1/auth/mfa/verify", tenant,
            new { mfaToken = challenge, code = CorrectTotp() }));
        correct.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the third attempt of a challenge the tenant refusal left untouched is still live");
    }

    // ─── Every other tenant refusal keeps its body ───────────────────────────

    [Fact]
    public async Task UsersMe_ShouldAnswer403WithoutMachineCode_WhenTokenTenantIsSuspended()
    {
        var tenant = NewTenant(TenantStatus.Active);
        var user = _factory.SaveUser(NewId("me-user"), tenant, UserRole.Agent, UserStatus.Active);
        var token = _factory.MintAccessToken(user);
        SetTenantStatus(tenant, TenantStatus.Suspended);
        using var client = _factory.CreateBearerClient(token);

        using var response = await client.GetAsync("/api/v1/users/me");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        AssertNotAMachineCode(await ErrorOfAsync(response), "a route outside sign-in keeps the tenant prose");
    }

    [Fact]
    public async Task Refresh_ShouldAnswer403WithoutMachineCode_WhenNamedTenantIsPendingDeletion()
    {
        var tenant = NewTenant(TenantStatus.PendingDeletion);
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Post("/api/v1/auth/refresh", tenant, new { }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await ErrorOfAsync(response);
        error.Should().NotBe(TenantPendingDeletionCode);
        AssertNotAMachineCode(error, "the refresh endpoint is not a sign-in endpoint and keeps the tenant prose");
    }

    [Fact]
    public async Task Login_ShouldAnswer404_WhenNamedTenantIsDeleted()
    {
        var tenant = NewTenant(TenantStatus.Deleted);
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Post("/api/v1/auth/login", tenant,
            new { email = "anyone@tenant.test", password = "AnyPassword123!" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "a Deleted tenant keeps its 404 on the sign-in endpoints too");
    }

    [Fact]
    public async Task Login_ShouldKeepTheBoundaryRefusalProse_WhenTenantHeaderContradictsTheBearerTenant()
    {
        var otherTenant = NewTenant(TenantStatus.Active);
        var caller = _factory.GetUser(AccountStatusApiFactory.CustomerAdminUserId, AccountStatusApiFactory.CustomerTenantId);
        caller.Should().NotBeNull();
        using var client = _factory.CreateBearerClient(_factory.MintAccessToken(caller!));

        using var response = await client.SendAsync(Post("/api/v1/auth/login", otherTenant,
            new { email = caller!.Email, password = "AnyPassword123!" }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await ErrorOfAsync(response);
        error.Should().NotBeNullOrWhiteSpace(because: "the boundary refusal carries a body");
        AssertNotAMachineCode(error, "only the account-status and tenant-status refusals are coded");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..12]}";

    private string NewTenant(TenantStatus status)
    {
        var tenantId = NewId("sirc");
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantStore>().UpsertAsync(new Tenant
        {
            TenantId = tenantId,
            Name = tenantId,
            Status = status,
            Type = TenantType.Customer,
            ParentTenantId = AccountStatusApiFactory.PlatformTenantId,
        }).AsTask().GetAwaiter().GetResult();
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

    private User SeedMfaUser(string tenantId)
    {
        var userId = NewId("mfa-user");
        return _factory.SeedUser(new User
        {
            UserId = EntityId.From(userId),
            TenantId = new TenantId(tenantId),
            Email = $"{userId}@sign-in-refusal.test",
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

    private static HttpRequestMessage Post<T>(string route, string tenantId, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Tenant-Id", tenantId);
        return request;
    }

    private static async Task AssertSoleErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        response.StatusCode.Should().Be(status);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(["error"],
            because: "the body carries the error member and nothing else");
        doc.RootElement.GetProperty("error").GetString().Should().Be(error);
    }

    private static async Task<string?> ErrorOfAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("error", out var e)
            && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;
    }

    private static void AssertNotAMachineCode(string? error, string because)
    {
        error.Should().NotBeNullOrWhiteSpace(because: "the refusal carries an error message");
        MachineCode().IsMatch(error!).Should().BeFalse(because);
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex MachineCode();

    private static string CorrectTotp() => new Totp(Base32Encoding.ToBytes(MfaSecret)).ComputeTotp(DateTime.UtcNow);

    /// <summary>A six-digit code outside the whole verification window (±2 steps, for clock edges).</summary>
    private static string WrongTotp()
    {
        var totp = new Totp(Base32Encoding.ToBytes(MfaSecret));
        var now = DateTime.UtcNow;
        var window = Enumerable.Range(-2, 5).Select(k => totp.ComputeTotp(now.AddSeconds(30.0 * k))).ToHashSet();
        for (var candidate = 0; ; candidate++)
        {
            var code = candidate.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            if (!window.Contains(code))
                return code;
        }
    }
}
