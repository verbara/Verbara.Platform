using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions.Execution;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// End-to-end account-status behavior on the HTTP surface: what an admin's status change does to
/// the user's sessions, audit trail and live connections, and how the paths that work from an
/// already-issued access token treat an account that is no longer Active.
/// </summary>
public sealed class AccountStatusEndpointTests : IClassFixture<AccountStatusApiFactory>
{
    private const string Customer = AccountStatusApiFactory.CustomerTenantId;
    private const string AccessRevokedEventType = "user.access_revoked";

    private readonly AccountStatusApiFactory _factory;

    public AccountStatusEndpointTests(AccountStatusApiFactory factory) => _factory = factory;

    // ─── Admin status change: PUT /admin/users/{id} ──────────────────────────

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Deactivated")]
    public async Task UpdateUser_ShouldRevokeEveryRefreshToken_WhenStatusMovesAwayFromActive(string newStatus)
    {
        var target = NewTargetUser();
        await SeedRefreshTokensAsync(target, count: 2);

        var response = await PutStatusAsync(target, newStatus);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ActiveRefreshTokensAsync(target)).Should().BeEmpty(
            because: "leaving Active must end the whole refresh-token lineage, not wait for it to expire");
    }

    [Fact]
    public async Task UpdateUser_ShouldRecordStatusChangeAudit_WhenStatusChanges()
    {
        var target = NewTargetUser();

        await PutStatusAsync(target, "Suspended");

        var entry = (await AuditEntriesAsync("user.status_changed"))
            .Should().ContainSingle(e => e.TargetId == target.UserId.Value).Subject;
        entry.ActorId.Should().Be(AccountStatusApiFactory.CustomerAdminUserId);
        entry.TargetType.Should().Be("User");
        entry.Metadata.Should().NotBeNull();
        entry.Metadata!["old_status"].Should().Be("Active");
        entry.Metadata["new_status"].Should().Be("Suspended");
    }

    [Fact]
    public async Task UpdateUser_ShouldPublishAccessRevokedEventForTheUser_WhenStatusMovesAwayFromActive()
    {
        var target = NewTargetUser();
        using var published = CapturePlatformEvents();

        await PutStatusAsync(target, "Suspended");

        published.Snapshot().Should().Contain(e => e.Type == AccessRevokedEventType
                && e.Metadata.TenantId == Customer
                && e.Metadata.UserId == target.UserId.Value,
            because: "Realtime and the SSE stream drop the user's live connections on this event");
    }

    [Fact]
    public async Task UpdateUser_ShouldAuditWithoutRevoking_WhenStatusReturnsToActive()
    {
        var target = NewTargetUser(UserStatus.Suspended);
        await SeedRefreshTokensAsync(target, count: 1);
        using var published = CapturePlatformEvents();

        var response = await PutStatusAsync(target, "Active");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ActiveRefreshTokensAsync(target)).Should().HaveCount(1, because: "re-activation revokes nothing");
        published.Snapshot().Should().NotContain(e => e.Type == AccessRevokedEventType);
        (await AuditEntriesAsync("user.status_changed")).Should().Contain(e =>
            e.TargetId == target.UserId.Value && e.Metadata != null && e.Metadata["new_status"] == "Active");
    }

    [Fact]
    public async Task UpdateUser_ShouldNotRevokeOrAudit_WhenStatusIsUnchanged()
    {
        var target = NewTargetUser();
        await SeedRefreshTokensAsync(target, count: 1);
        using var published = CapturePlatformEvents();

        using var client = AdminClient();
        var response = await client.PutAsJsonAsync(
            $"/api/v1/admin/users/{target.UserId.Value}", new { displayName = "Renamed" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ActiveRefreshTokensAsync(target)).Should().HaveCount(1);
        published.Snapshot().Should().NotContain(e => e.Type == AccessRevokedEventType);
        (await AuditEntriesAsync("user.status_changed")).Should().NotContain(e => e.TargetId == target.UserId.Value);
    }

    // ─── A save from a snapshot read before the change ───────────────────────
    //
    // Every user write outside PUT /admin/users/{id} loads the user, changes its own fields and saves
    // the whole object: a failed sign-in recording its attempt, a password change, the last-login
    // flush. One that read the account before the admin's change and saves after it must not put the
    // old status or role back. The snapshot is a copy, as PostgresUserStore materialises a fresh
    // object on every read (on another node, or once the cache entry was dropped).

    [Fact]
    public async Task UpdateUser_ShouldKeepTheSuspension_WhenAFailedSignInFromAnEarlierSnapshotSavesAfterIt()
    {
        var target = NewTargetUser(password: TargetPassword);
        var earlierSnapshot = SnapshotOf(target);

        (await PutStatusAsync(target, "Suspended")).StatusCode.Should().Be(HttpStatusCode.OK);
        await RecordFailedSignInAsync(earlierSnapshot);

        var storedStatus = _factory.GetUser(target.UserId.Value, Customer)!.Status;
        using var signIn = await SignInAsync(target, TargetPassword);
        using (new AssertionScope())
        {
            storedStatus.Should().Be(UserStatus.Suspended,
                because: "a save from a snapshot read before the suspension must not undo it");
            signIn.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                because: "the suspended account must stay unable to sign in");
        }
    }

    [Fact]
    public async Task UpdateUser_ShouldKeepTheRoleChange_WhenAFailedSignInFromAnEarlierSnapshotSavesAfterIt()
    {
        var target = NewTargetUser(role: UserRole.Admin);
        var earlierSnapshot = SnapshotOf(target);

        using var client = AdminClient();
        (await client.PutAsJsonAsync($"/api/v1/admin/users/{target.UserId.Value}", new { role = "Agent" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await RecordFailedSignInAsync(earlierSnapshot);

        _factory.GetUser(target.UserId.Value, Customer)!.Role.Should().Be(UserRole.Agent,
            because: "a save from a snapshot read before the demotion must not restore the old role");
    }

    // ─── Admin delete: DELETE /admin/users/{id} ──────────────────────────────

    [Fact]
    public async Task DeleteUser_ShouldPublishAccessRevokedEvent_WhenTheUserIsDeleted()
    {
        var target = NewTargetUser();
        using var published = CapturePlatformEvents();

        using var client = AdminClient();
        var response = await client.DeleteAsync($"/api/v1/admin/users/{target.UserId.Value}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        published.Snapshot().Should().Contain(e => e.Type == AccessRevokedEventType
            && e.Metadata.UserId == target.UserId.Value,
            because: "a deleted user's live connections must not outlive the account");
    }

    // ─── GDPR erasure: POST /admin/gdpr/purge-user ───────────────────────────

    [Fact]
    public async Task PurgeUserData_ShouldPublishAccessRevokedEvent_WhenTheUserIsPurged()
    {
        var target = NewTargetUser();
        using var published = CapturePlatformEvents();

        using var client = AdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/gdpr/purge-user")
        {
            Content = JsonContent.Create(new { userId = target.UserId.Value, reason = "erasure request" }),
        };
        request.Headers.Add("X-Confirm-Purge", "true");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.GetUser(target.UserId.Value, Customer).Should().BeNull(because: "the purge deletes the account");
        published.Snapshot().Should().Contain(e => e.Type == AccessRevokedEventType
            && e.Metadata.TenantId == Customer
            && e.Metadata.UserId == target.UserId.Value,
            because: "a purged user's live connections must not outlive the account, exactly as on DELETE");
    }

    // ─── Impersonation start (mints a 30-minute token for the caller) ────────

    [Fact]
    public async Task StartImpersonation_ShouldReturn403_WhenCallerAccountIsSuspended()
    {
        // The access token was issued before the suspension and is still inside its lifetime;
        // it must not be exchanged for a longer-lived impersonation token.
        var admin = _factory.SaveUser(
            $"suspended-platform-admin-{Guid.NewGuid():N}", AccountStatusApiFactory.PlatformTenantId,
            UserRole.Admin, UserStatus.Suspended);
        using var client = _factory.CreateBearerClient(_factory.MintAccessToken(admin));

        var response = await client.PostAsJsonAsync(
            "/api/v1/management/impersonate", new { targetTenantId = Customer, readOnly = false });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task StartImpersonation_ShouldIssueToken_WhenCallerAccountIsActive()
    {
        var admin = _factory.SaveUser(
            $"active-platform-admin-{Guid.NewGuid():N}", AccountStatusApiFactory.PlatformTenantId,
            UserRole.Admin, UserStatus.Active);
        using var client = _factory.CreateBearerClient(_factory.MintAccessToken(admin));

        var response = await client.PostAsJsonAsync(
            "/api/v1/management/impersonate", new { targetTenantId = Customer, readOnly = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── SSE stream open: GET /events/stream ─────────────────────────────────

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task StreamEvents_ShouldReturn403_WhenAccountIsNotActive(UserStatus status)
    {
        var user = NewTargetUser(status);

        using var response = await OpenEventStreamAsync(_factory.MintAccessToken(user));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "a still-valid access token must not open a new live stream for an inactive account");
    }

    [Fact]
    public async Task StreamEvents_ShouldOpenTheStream_WhenAccountIsActive()
    {
        var user = NewTargetUser();

        using var response = await OpenEventStreamAsync(_factory.MintAccessToken(user));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
    }

    // ─── Internal status lookup Realtime calls on every hub connect ──────────

    [Theory]
    [InlineData(UserStatus.Active, true)]
    [InlineData(UserStatus.Suspended, false)]
    [InlineData(UserStatus.Deactivated, false)]
    public async Task GetUserAccess_ShouldReportAllowedOnlyForActive_WhenTheUserExists(UserStatus status, bool expectedAllowed)
    {
        var user = NewTargetUser(status);
        using var client = _factory.CreateServiceClient();

        var response = await client.GetAsync($"/api/v1/internal/user-access/{Customer}/{user.UserId.Value}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAllowedAsync(response)).Should().Be(expectedAllowed);
    }

    [Fact]
    public async Task GetUserAccess_ShouldReportNotAllowed_WhenUserDoesNotExist()
    {
        using var client = _factory.CreateServiceClient();

        var response = await client.GetAsync($"/api/v1/internal/user-access/{Customer}/no-such-user-{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "an unknown user is a definite answer (not allowed), distinct from an unreachable endpoint");
        (await ReadAllowedAsync(response)).Should().BeFalse();
    }

    [Fact]
    public async Task GetUserAccess_ShouldReturn401_WhenServiceKeyIsMissing()
    {
        var user = NewTargetUser();
        using var client = _factory.CreateServiceClient(serviceKey: null);

        var response = await client.GetAsync($"/api/v1/internal/user-access/{Customer}/{user.UserId.Value}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private const string TargetPassword = "Target-Passw0rd!";

    private User NewTargetUser(
        UserStatus status = UserStatus.Active, UserRole role = UserRole.Agent, string? password = null) =>
        _factory.SaveUser($"acct-status-target-{Guid.NewGuid():N}", Customer, role, status, password);

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
        MfaRecoveryCodes = user.MfaRecoveryCodes,
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

    private async Task<HttpResponseMessage> PutStatusAsync(User target, string status)
    {
        using var client = AdminClient();
        return await client.PutAsJsonAsync($"/api/v1/admin/users/{target.UserId.Value}", new { status });
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

    private EventCapture CapturePlatformEvents() =>
        new(_factory.Services.GetRequiredService<PlatformEventBus>());

    private async Task<HttpResponseMessage> OpenEventStreamAsync(string accessToken)
    {
        var client = _factory.CreateBearerClient(accessToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events/stream");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
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

    /// <summary>Records every <see cref="PlatformEvent"/> published while it is alive.</summary>
    private sealed class EventCapture : IObserver<PlatformEvent>, IDisposable
    {
        private readonly List<PlatformEvent> _events = [];
        private readonly IDisposable _subscription;

        public EventCapture(PlatformEventBus bus) => _subscription = bus.Events.Subscribe(this);

        public List<PlatformEvent> Snapshot()
        {
            lock (_events)
                return _events.ToList();
        }

        public void OnNext(PlatformEvent value)
        {
            lock (_events)
                _events.Add(value);
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }

        public void Dispose() => _subscription.Dispose();
    }
}
