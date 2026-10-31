using System.Security.Claims;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Notifications;
using Verbara.Platform.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OtpNet;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// agent-console-contract-debt (C9, H4): the three security notifications link to <c>/profile/security</c>. Only the
/// link moves: <c>title</c> and <c>body</c> are compared with <c>security-notification-link.v1.json</c>, and the
/// audience and severity with the registry. The audience is still admin and system_admin, so this is not a fix for an
/// Agent. The handlers run against a capturing <see cref="INotificationService"/>, because a real one would resolve
/// recipients and persist a row, which says nothing about the literals passed in.
/// </summary>
public sealed class SecurityNotificationLinkTests
{
    private const string TenantId = "tenant1";
    private const string UserId = "user1";
    private const string Email = "test@example.com";
    private const string Password = "CurrentPassword123!";

    private static readonly string s_passwordHash = PasswordService.HashPassword(Password);
    private static readonly string[] s_adminRoles = ["admin", "system_admin"];

    private sealed record CapturedNotification(string TenantId, string Type, string Title, string Body, string? ActionUrl);

    private sealed class CapturingNotificationService : INotificationService
    {
        public List<CapturedNotification> Created { get; } = [];

        public Task CreateAsync(
            string tenantId,
            string type,
            string title,
            string body,
            string? actionUrl = null,
            CancellationToken ct = default)
        {
            Created.Add(new CapturedNotification(tenantId, type, title, body, actionUrl));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ChangePassword_ShouldLinkToProfileSecurityWithFixtureText_WhenPasswordChanges()
    {
        var user = BuildUser();
        var userStore = StoreOf(user);
        var authConfig = Substitute.For<ITenantAuthConfigStore>();
        authConfig.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(new TenantAuthConfig { TenantId = TenantId }));
        var notifications = new CapturingNotificationService();

        var result = await AuthEndpoints.ChangePassword(
            new ChangePasswordRequest(Password, "NewValidPassword456!"),
            BuildHttpContext(),
            userStore,
            authConfig,
            new AuthEventService(Substitute.For<IAuthEventStore>()),
            notifications,
            CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();
        AssertMatchesFixture(notifications, "security.password_changed");
    }

    [Fact]
    public async Task MfaConfirm_ShouldLinkToProfileSecurityWithFixtureText_WhenMfaIsEnabled()
    {
        var secret = KeyGeneration.GenerateRandomKey(20);
        var user = BuildUser();
        user.MfaSecret = Base32Encoding.ToString(secret);
        var notifications = new CapturingNotificationService();

        var result = await AuthEndpoints.MfaConfirm(
            new MfaConfirmRequest(new Totp(secret).ComputeTotp(DateTime.UtcNow)),
            BuildHttpContext(),
            StoreOf(user),
            new AuthEventService(Substitute.For<IAuthEventStore>()),
            notifications,
            CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();
        AssertMatchesFixture(notifications, "security.mfa_enabled");
    }

    [Fact]
    public async Task MfaDisable_ShouldLinkToProfileSecurityWithFixtureText_WhenMfaIsDisabled()
    {
        var user = BuildUser();
        user.MfaEnabled = true;
        user.MfaConfirmedAt = DateTimeOffset.UtcNow;
        var authConfig = Substitute.For<ITenantAuthConfigStore>();
        authConfig.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(new TenantAuthConfig { TenantId = TenantId, MfaPolicy = "optional" }));
        var notifications = new CapturingNotificationService();

        var result = await AuthEndpoints.MfaDisable(
            new MfaDisableRequest(Password),
            BuildHttpContext(),
            StoreOf(user),
            authConfig,
            new AuthEventService(Substitute.For<IAuthEventStore>()),
            notifications,
            CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();
        AssertMatchesFixture(notifications, "security.mfa_disabled");
    }

    [Theory]
    [InlineData("security.password_changed", NotificationSeverity.Info)]
    [InlineData("security.mfa_enabled", NotificationSeverity.Info)]
    [InlineData("security.mfa_disabled", NotificationSeverity.Warning)]
    public void Get_ShouldKeepAudienceAndSeverity_WhenTheLinkMoves(string type, NotificationSeverity severity)
    {
        var info = NotificationTypeRegistry.Get(type);

        info.Should().NotBeNull();
        info!.Category.Should().Be(NotificationCategory.Security);
        info.Severity.Should().Be(severity);
        info.TargetRoles.Should().BeEquivalentTo(s_adminRoles);
    }

    private static void AssertMatchesFixture(CapturingNotificationService notifications, string type)
    {
        var fixture = LinkFixture();
        var expected = fixture["notifications"]!.AsArray().Single(entry => Text(entry, "type") == type);
        var after = Text(fixture["actionUrl"], "after");

        var created = notifications.Created.Should().ContainSingle().Subject;
        created.TenantId.Should().Be(TenantId);
        created.Type.Should().Be(type);
        created.Title.Should().Be(Text(expected, "title"));
        created.Body.Should().Be(Text(expected, "body").Replace("<email>", Email, StringComparison.Ordinal));
        created.ActionUrl.Should().Be(Text(expected, "actionUrl")).And.Be(after);
    }

    private static JsonObject LinkFixture()
    {
        var file = Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", "security-notification-link.v1.json");
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    private static string Text(JsonNode? node, string name) => node![name]!.GetValue<string>();

    private static IUserStore StoreOf(User user)
    {
        var store = Substitute.For<IUserStore>();
        store.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));
        SubstituteUserWrites.ApplyTo(store, user);
        return store;
    }

    private static User BuildUser() => new()
    {
        UserId = EntityId.From(UserId),
        TenantId = new TenantId(TenantId),
        Email = Email,
        DisplayName = "Test User",
        Role = UserRole.Admin,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        PasswordHash = s_passwordHash,
    };

    private static DefaultHttpContext BuildHttpContext() => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tid", TenantId), new Claim("sub", UserId), new Claim("user_id", UserId)],
            "TestScheme")),
    };
}
