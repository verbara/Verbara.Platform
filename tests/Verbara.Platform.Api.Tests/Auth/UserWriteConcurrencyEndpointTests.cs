using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions.Execution;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// User writes that race on the HTTP surface. Each write that is not an admin's change of role or
/// status loads the user, changes its own fields and stores them: a failed sign-in recording its
/// attempt, a password change, the deferred last-login flush. One that read the account before
/// another change and stores after it must neither bring a deleted account back nor put back the
/// password, MFA state or failed-attempt count the other change replaced.
/// </summary>
/// <remarks>
/// The snapshot each test holds is a separate copy, as a fresh read of the row returns (another
/// node, or a cache entry that was dropped), and the real host stack stores it:
/// <see cref="AccountLockoutService"/> over the cached store over the in-memory store.
/// </remarks>
public sealed class UserWriteConcurrencyEndpointTests : IClassFixture<AccountStatusApiFactory>
{
    private const string Customer = AccountStatusApiFactory.CustomerTenantId;
    private const string TargetPassword = "Target-Passw0rd!";

    private readonly AccountStatusApiFactory _factory;

    public UserWriteConcurrencyEndpointTests(AccountStatusApiFactory factory) => _factory = factory;

    // ─── A deleted account stays deleted ─────────────────────────────────────

    [Fact]
    public async Task DeleteUser_ShouldStayDeleted_WhenAFailedSignInFromAnEarlierSnapshotSavesAfterTheDelete()
    {
        var target = NewTargetUser(password: TargetPassword);
        var earlierSnapshot = SnapshotOf(target);

        (await DeleteAsync(target)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await RecordFailedSignInAsync(earlierSnapshot);

        using var admin = AdminClient();
        var response = await admin.GetAsync($"/api/v1/admin/users/{target.UserId.Value}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "recording a failed attempt for a copy read before the delete must not create the account again");
    }

    [Fact]
    public async Task Refresh_ShouldReturn401_WhenTheUserWasDeletedAndAFailedSignInFromAnEarlierSnapshotFollowed()
    {
        var target = NewTargetUser(password: TargetPassword);
        var (rawRefreshToken, _) = await _factory.Services.GetRequiredService<RefreshTokenService>()
            .GenerateAsync(target.UserId.Value, Customer, "10.0.0.1", "test", CancellationToken.None);
        var earlierSnapshot = SnapshotOf(target);

        (await DeleteAsync(target)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await RecordFailedSignInAsync(earlierSnapshot);

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", $"refresh_token={rawRefreshToken}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "a refresh token issued before the delete must not mint access for the deleted account");
    }

    [Fact]
    public async Task ApiKey_ShouldReturn401_WhenItsOwnerWasDeletedAndAFailedSignInFromAnEarlierSnapshotFollowed()
    {
        var target = NewTargetUser(password: TargetPassword);
        var rawKey = await SeedUserBoundApiKeyAsync(target);
        using (var beforeDelete = ApiKeyClient(rawKey))
        {
            (await beforeDelete.GetAsync("/api/v1/auth/sessions")).StatusCode.Should().Be(HttpStatusCode.OK,
                because: "the key works while its owner exists, so the refusal below is caused by the delete");
        }
        var earlierSnapshot = SnapshotOf(target);

        (await DeleteAsync(target)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await RecordFailedSignInAsync(earlierSnapshot);

        using var client = ApiKeyClient(rawKey);
        var response = await client.GetAsync("/api/v1/auth/sessions");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "a user-bound key has no one to act as once its owner is deleted");
    }

    [Fact]
    public async Task PurgeUserData_ShouldStayPurged_WhenAFailedSignInFromAnEarlierSnapshotSavesAfterThePurge()
    {
        var target = NewTargetUser(password: TargetPassword);
        var earlierSnapshot = SnapshotOf(target);

        (await PurgeAsync(target)).StatusCode.Should().Be(HttpStatusCode.OK);
        await RecordFailedSignInAsync(earlierSnapshot);

        _factory.GetUser(target.UserId.Value, Customer).Should().BeNull(
            because: "a failed attempt recorded for a copy read before the erasure must not restore the account");
    }

    // ─── Delete and erasure end the refresh-token lineage ────────────────────

    [Fact]
    public async Task DeleteUser_ShouldRevokeEveryRefreshToken_WhenTheUserIsDeleted()
    {
        var target = NewTargetUser();
        await SeedRefreshTokensAsync(target, count: 2);

        (await DeleteAsync(target)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ActiveRefreshTokensAsync(target)).Should().BeEmpty(
            because: "a deleted account's sessions end with it, not when their tokens expire");
    }

    [Fact]
    public async Task DeleteUser_ShouldRecordUserDeletedAudit_WhenTheUserIsDeleted()
    {
        var target = NewTargetUser();
        await SeedRefreshTokensAsync(target, count: 2);

        (await DeleteAsync(target)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var entry = (await AuditEntriesAsync("user.deleted"))
            .Should().ContainSingle(e => e.TargetId == target.UserId.Value).Subject;
        using (new AssertionScope())
        {
            entry.ActorId.Should().Be(AccountStatusApiFactory.CustomerAdminUserId);
            entry.TargetType.Should().Be("User");
            entry.Metadata.Should().NotBeNull();
            entry.Metadata!["revoked_sessions"].Should().Be("2");
        }
    }

    [Fact]
    public async Task PurgeUserData_ShouldRevokeEveryRefreshToken_WhenTheUserIsPurged()
    {
        var target = NewTargetUser();
        await SeedRefreshTokensAsync(target, count: 2);

        (await PurgeAsync(target)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ActiveRefreshTokensAsync(target)).Should().BeEmpty(
            because: "an erased account's sessions end with it, exactly as on DELETE /admin/users/{id}");
    }

    // ─── A stale copy does not put back what another write replaced ──────────

    [Fact]
    public async Task ChangePassword_ShouldKeepTheNewPassword_WhenAFailedSignInFromAnEarlierSnapshotSavesAfterIt()
    {
        const string newPassword = "Changed-Passw0rd!";
        var target = NewTargetUser(password: TargetPassword);
        var earlierSnapshot = SnapshotOf(target);

        using (var self = _factory.CreateBearerClient(_factory.MintAccessToken(target)))
        {
            (await self.PostAsJsonAsync("/api/v1/auth/change-password",
                    new { oldPassword = TargetPassword, newPassword }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        await RecordFailedSignInAsync(earlierSnapshot);

        using var withNew = await SignInAsync(target, newPassword);
        using var withOld = await SignInAsync(target, TargetPassword);
        using (new AssertionScope())
        {
            withNew.StatusCode.Should().Be(HttpStatusCode.OK, because: "the changed password stays the account's password");
            withOld.StatusCode.Should().Be(HttpStatusCode.Unauthorized, because: "the replaced password must not come back");
        }
    }

    [Fact]
    public async Task MfaDisable_ShouldStayDisabled_WhenAFailedSignInFromAnEarlierSnapshotSavesAfterIt()
    {
        var target = SeedMfaUser();
        var earlierSnapshot = SnapshotOf(target);

        using (var self = _factory.CreateBearerClient(_factory.MintAccessToken(target)))
        using (var disable = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/auth/mfa")
        {
            Content = JsonContent.Create(new { password = TargetPassword }),
        })
        {
            (await self.SendAsync(disable)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        await RecordFailedSignInAsync(earlierSnapshot);

        var stored = _factory.GetUser(target.UserId.Value, Customer)!;
        using (new AssertionScope())
        {
            stored.MfaEnabled.Should().BeFalse(because: "a copy read while MFA was on must not switch it back on");
            stored.MfaSecret.Should().BeNull(because: "the cleared secret must not come back");
            stored.MfaRecoveryCodes.Should().BeNull(because: "the cleared recovery codes must not come back");
        }
    }

    // ─── The failed-attempt counter ──────────────────────────────────────────

    [Fact]
    public async Task RecordFailedAttempt_ShouldCountEveryAttempt_WhenTwoSnapshotsEachRecordOne()
    {
        var target = NewTargetUser(password: TargetPassword);
        var first = SnapshotOf(target);
        var second = SnapshotOf(target);

        await RecordFailedSignInAsync(first);
        await RecordFailedSignInAsync(second);

        _factory.GetUser(target.UserId.Value, Customer)!.FailedLoginAttempts.Should().Be(2,
            because: "two failed attempts are two, whatever copy of the account each one read");
    }

    [Fact]
    public async Task RecordFailedAttempt_ShouldLockTheAccount_WhenParallelFailuresReachTheThreshold()
    {
        // The default tenant policy locks at 5 failed attempts.
        const int attempts = 20;
        var target = NewTargetUser(password: TargetPassword);
        var snapshots = Enumerable.Range(0, attempts).Select(_ => SnapshotOf(target)).ToList();

        await Task.WhenAll(snapshots.Select(RecordFailedSignInAsync));

        var stored = _factory.GetUser(target.UserId.Value, Customer)!;
        using (new AssertionScope())
        {
            stored.FailedLoginAttempts.Should().Be(attempts, because: "no attempt may be lost under parallel guessing");
            stored.IsLockedOut(DateTimeOffset.UtcNow).Should().BeTrue(because: "the threshold was crossed");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private User NewTargetUser(UserRole role = UserRole.Agent, string? password = null) =>
        _factory.SaveUser($"user-write-target-{Guid.NewGuid():N}", Customer, role, UserStatus.Active, password);

    private User SeedMfaUser() => _factory.SeedUser(new User
    {
        UserId = EntityId.From($"user-write-mfa-{Guid.NewGuid():N}"),
        TenantId = new TenantId(Customer),
        Email = $"user-write-mfa-{Guid.NewGuid():N}@acct-status.test",
        DisplayName = "MFA target",
        Role = UserRole.Agent,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        PasswordHash = PasswordService.HashPassword(TargetPassword),
        MfaEnabled = true,
        MfaSecret = "JBSWY3DPEHPK3PXP",
        MfaRecoveryCodes = MfaService.HashRecoveryCodes(["TARGETCODE0001"]).ToList(),
        MfaConfirmedAt = DateTimeOffset.UtcNow.AddDays(-30),
    });

    /// <summary>A separate copy of <paramref name="user"/>, as a fresh read of its row would return.</summary>
    private static User SnapshotOf(User user) => new()
    {
        UserId = user.UserId,
        TenantId = user.TenantId,
        Email = user.Email,
        DisplayName = user.DisplayName,
        Role = user.Role,
        Status = user.Status,
        CreatedAt = user.CreatedAt,
        UpdatedAt = user.UpdatedAt,
        CreatedBy = user.CreatedBy,
        UpdatedBy = user.UpdatedBy,
        PasswordHash = user.PasswordHash,
        MfaEnabled = user.MfaEnabled,
        MfaSecret = user.MfaSecret,
        MfaRecoveryCodes = user.MfaRecoveryCodes?.ToList(),
        MfaConfirmedAt = user.MfaConfirmedAt,
        EmailVerified = user.EmailVerified,
        FailedLoginAttempts = user.FailedLoginAttempts,
        LockedUntil = user.LockedUntil,
        PasswordChangedAt = user.PasswordChangedAt,
        LastLoginAt = user.LastLoginAt,
        AuthProvider = user.AuthProvider,
        ExternalId = user.ExternalId,
        OidcSubject = user.OidcSubject,
    };

    /// <summary>Records a failed sign-in for <paramref name="snapshot"/> the way POST /auth/login does.</summary>
    private Task RecordFailedSignInAsync(User snapshot) =>
        _factory.Services.GetRequiredService<AccountLockoutService>()
            .RecordFailedAttemptAsync(snapshot, "10.0.0.9", "stale-snapshot-test", CancellationToken.None);

    private async Task<HttpResponseMessage> SignInAsync(User user, string password)
    {
        using var client = _factory.CreateClient();
        return await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            tenantId = user.TenantId.Value,
            email = user.Email,
            password,
        });
    }

    private HttpClient AdminClient()
    {
        var admin = _factory.GetUser(AccountStatusApiFactory.CustomerAdminUserId, Customer)!;
        return _factory.CreateBearerClient(_factory.MintAccessToken(admin));
    }

    private async Task<HttpResponseMessage> DeleteAsync(User target)
    {
        using var client = AdminClient();
        return await client.DeleteAsync($"/api/v1/admin/users/{target.UserId.Value}");
    }

    private async Task<HttpResponseMessage> PurgeAsync(User target)
    {
        using var client = AdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/gdpr/purge-user")
        {
            Content = JsonContent.Create(new { userId = target.UserId.Value, reason = "erasure request" }),
        };
        request.Headers.Add("X-Confirm-Purge", "true");
        return await client.SendAsync(request);
    }

    private async Task<string> SeedUserBoundApiKeyAsync(User owner)
    {
        var rawKey = $"user-write-key-{Guid.NewGuid():N}";
        await _factory.Services.GetRequiredService<IApiKeyStore>().SaveAsync(new ApiKey
        {
            KeyId = EntityId.New(),
            TenantId = owner.TenantId,
            Name = "user-bound test key",
            HashedKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))),
            Scopes = ["*"],
            UserId = owner.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        return rawKey;
    }

    private HttpClient ApiKeyClient(string rawKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawKey);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Customer);
        return client;
    }

    private async Task SeedRefreshTokensAsync(User user, int count)
    {
        var refreshTokens = _factory.Services.GetRequiredService<RefreshTokenService>();
        for (var i = 0; i < count; i++)
            await refreshTokens.GenerateAsync(user.UserId.Value, user.TenantId.Value, "10.0.0.1", "test", CancellationToken.None);
    }

    private Task<IReadOnlyList<RefreshToken>> ActiveRefreshTokensAsync(User user) =>
        _factory.Services.GetRequiredService<IRefreshTokenStore>()
            .GetActiveByUserAsync(user.TenantId.Value, user.UserId.Value, CancellationToken.None);

    private async Task<IReadOnlyList<AuditEntry>> AuditEntriesAsync(string action)
    {
        using var scope = _factory.Services.CreateScope();
        var page = await scope.ServiceProvider.GetRequiredService<IAuditStore>().SearchAsync(
            new TenantId(Customer), new AuditQuery(Action: action, Page: 1, PageSize: 200), CancellationToken.None);
        return page.Items;
    }
}
