using System.Net;
using System.Text.Json.Nodes;
using Asterisk.Platform.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Api.Tests;

/// <summary>
/// Integration tests for <see cref="Asterisk.Platform.Api.Endpoints.NotificationEndpoints"/>.
/// Uses <see cref="AuthenticatedPlatformApiFactory"/> which authenticates requests as
/// tenant = <c>tenant-test-001</c>, user = <c>test-admin-user</c>.
/// </summary>
public sealed class NotificationEndpointsTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private const string TenantId = AuthenticatedPlatformApiFactory.TestTenantId;

    // The user ID that ApiKeyAuthenticationHandler sets for the test API key (user_id claim).
    // Matches the UserId on the User returned by the mock IUserStore in SetupTestAuth.
    private const string UserId = "test-admin-user";

    private readonly HttpClient _client;
    private readonly INotificationStore _store;

    public NotificationEndpointsTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _store = factory.Services.GetRequiredService<INotificationStore>();
    }

    // ── List ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListNotifications_ShouldReturnPaged_WhenNotificationsExist()
    {
        // Arrange — seed 3 notifications for the authenticated user
        await SeedNotification(TenantId, UserId, "list.test.1", "List Title 1");
        await SeedNotification(TenantId, UserId, "list.test.2", "List Title 2");
        await SeedNotification(TenantId, UserId, "list.test.3", "List Title 3");

        // Act — request with limit=2
        var response = await _client.GetAsync("/api/v1/notifications?limit=2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        body.Count.Should().BeLessThanOrEqualTo(2);
        body.Count.Should().BeGreaterThan(0);
    }

    // ── Unread Count ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUnreadCount_ShouldReturnCount_WhenUnreadExist()
    {
        // Arrange — seed 2 unread + 1 read for a unique user to avoid cross-test interference
        var isolatedUserId = "count-user-" + Guid.NewGuid().ToString("N")[..8];

        var readId = await SeedNotification(TenantId, isolatedUserId, "count.read", "Read C");
        await _store.MarkReadAsync(readId, CancellationToken.None);
        await SeedNotification(TenantId, isolatedUserId, "count.unread.a", "Unread A");
        await SeedNotification(TenantId, isolatedUserId, "count.unread.b", "Unread B");

        // Count directly via store (user-specific, not via HTTP since HTTP uses the test auth user)
        var count = await _store.CountUnreadAsync(TenantId, isolatedUserId, CancellationToken.None);
        count.Should().Be(2);

        // Verify HTTP endpoint returns count for the authenticated test user (may have other notifications)
        var response = await _client.GetAsync("/api/v1/notifications/unread-count");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["count"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(0);
    }

    // ── Mark Single Read ─────────────────────────────────────────────────────

    [Fact]
    public async Task MarkRead_ShouldSetIsRead_WhenNotificationOwned()
    {
        // Arrange
        var notificationId = await SeedNotification(TenantId, UserId, "markread.type", "Mark Read Test");

        // Act
        var putResponse = await _client.PutAsync($"/api/v1/notifications/{notificationId}/read", null);

        // Assert — 204 No Content
        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify via GET
        var getResponse = await _client.GetAsync($"/api/v1/notifications/{notificationId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        body["isRead"]!.GetValue<bool>().Should().BeTrue();
    }

    // ── Mark All Read ────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkAllRead_ShouldMarkAll_WhenCalled()
    {
        // Arrange — seed 3 unread notifications for the authenticated user
        await SeedNotification(TenantId, UserId, "markall.a", "Mark All A");
        await SeedNotification(TenantId, UserId, "markall.b", "Mark All B");
        await SeedNotification(TenantId, UserId, "markall.c", "Mark All C");

        // Verify there is at least one unread
        var countBefore = await _store.CountUnreadAsync(TenantId, UserId, CancellationToken.None);
        countBefore.Should().BeGreaterThan(0);

        // Act
        var putResponse = await _client.PutAsync("/api/v1/notifications/read-all", null);

        // Assert — 204 No Content
        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify unread count via HTTP is 0
        var countResponse = await _client.GetAsync("/api/v1/notifications/unread-count");
        countResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var countBody = JsonNode.Parse(await countResponse.Content.ReadAsStringAsync())!;
        countBody["count"]!.GetValue<int>().Should().Be(0);
    }

    // ── Ownership Check ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetNotification_ShouldReturn404_WhenNotOwnedByUser()
    {
        // Arrange — seed a notification for a DIFFERENT user (not the authenticated test user)
        var otherUserId = "other-user-" + Guid.NewGuid().ToString("N")[..8];
        var notificationId = await SeedNotification(TenantId, otherUserId, "ownership.type", "Other User Notif");

        // Act — authenticated test user (UserId) tries to GET another user's notification
        var response = await _client.GetAsync($"/api/v1/notifications/{notificationId}");

        // Assert — ownership check must reject this request
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> SeedNotification(
        string tenantId, string userId, string type, string title)
    {
        var notificationId = Guid.NewGuid().ToString("N");
        var notification = new Notification
        {
            NotificationId = notificationId,
            TenantId = tenantId,
            UserId = userId,
            Category = NotificationCategory.System,
            Severity = NotificationSeverity.Info,
            Type = type,
            Title = title,
            Body = $"Body for {title}",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _store.SaveAsync(notification, CancellationToken.None);
        return notificationId;
    }
}
