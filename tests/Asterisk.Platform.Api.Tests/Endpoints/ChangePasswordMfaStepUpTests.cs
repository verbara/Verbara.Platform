using System.Security.Claims;
using System.Text.Json;
using Asterisk.Platform.Api.Endpoints;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Notifications;
using Asterisk.Platform.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OtpNet;

namespace Asterisk.Platform.Api.Tests.Endpoints;

/// <summary>
/// Regression coverage for Frente D (v1.9.2): MFA step-up gate on /auth/change-password.
/// Users with MfaEnabled == true must include a valid TOTP code in the same request.
/// Users without MFA enrolled are unaffected.
/// </summary>
public sealed class ChangePasswordMfaStepUpTests
{
    private const string TestTenantId = "tenant1";
    private const string TestUserId = "user1";
    private const string TestPassword = "CurrentPassword123!";
    private const string NewValidPassword = "NewValidPassword456!";

    private static readonly string s_knownHash = PasswordService.HashPassword(TestPassword);

    // ─── T1: MFA enrolled, code absent → 401 + step-up body ────────────────

    [Fact]
    public async Task ChangePassword_ShouldReturn401WithStepUpRequired_WhenUserMfaEnabledAndCodeMissing()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var notifications = Substitute.For<INotificationService>();

        var (user, _) = BuildMfaUser();
        var originalHash = user.PasswordHash;

        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(new TenantAuthConfig { TenantId = TestTenantId }));

        // MfaCode omitted (default null)
        var request = new ChangePasswordRequest(TestPassword, NewValidPassword);
        var result = await AuthEndpoints.ChangePassword(
            request,
            BuildHttpContext(),
            userStore,
            tenantAuthConfigStore,
            new AuthEventService(authEventStore),
            notifications,
            CancellationToken.None);

        // Status 401 with structured body
        result.Should().BeAssignableTo<IResult>();
        var jsonResult = result as Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<MfaStepUpRequiredResponse>;
        jsonResult.Should().NotBeNull("expected a Json result with MfaStepUpRequiredResponse");
        jsonResult!.StatusCode.Should().Be(401);
        jsonResult.Value!.MfaStepUpRequired.Should().BeTrue();
        jsonResult.Value.Reason.Should().NotBeNullOrWhiteSpace();

        // Password must remain unchanged
        user.PasswordHash.Should().Be(originalHash);

        // Failure event must be logged
        await authEventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e.EventType == AuthEventTypes.PasswordChangeFailure),
            Arg.Any<CancellationToken>());

        // No notification for an unsuccessful change
        await notifications.DidNotReceiveWithAnyArgs().CreateAsync(
            default!, default!, default!, default!, default, default);
    }

    // ─── T2: MFA enrolled, code invalid → 401 Unauthorized ─────────────────

    [Fact]
    public async Task ChangePassword_ShouldReturn401_WhenUserMfaEnabledAndCodeInvalid()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var notifications = Substitute.For<INotificationService>();

        var (user, _) = BuildMfaUser();
        var originalHash = user.PasswordHash;

        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(new TenantAuthConfig { TenantId = TestTenantId }));

        var request = new ChangePasswordRequest(TestPassword, NewValidPassword, MfaCode: "000000");
        var result = await AuthEndpoints.ChangePassword(
            request,
            BuildHttpContext(),
            userStore,
            tenantAuthConfigStore,
            new AuthEventService(authEventStore),
            notifications,
            CancellationToken.None);

        result.Should().BeOfType<UnauthorizedHttpResult>();
        user.PasswordHash.Should().Be(originalHash);

        await authEventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e.EventType == AuthEventTypes.PasswordChangeFailure),
            Arg.Any<CancellationToken>());
    }

    // ─── T3: MFA enrolled, code valid → 200 + password changed ─────────────

    [Fact]
    public async Task ChangePassword_ShouldSucceed_WhenUserMfaEnabledAndCodeValid()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var notifications = Substitute.For<INotificationService>();

        var (user, secretBytes) = BuildMfaUser();
        var originalHash = user.PasswordHash;

        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(new TenantAuthConfig { TenantId = TestTenantId }));

        var validCode = new Totp(secretBytes).ComputeTotp(DateTime.UtcNow);
        var request = new ChangePasswordRequest(TestPassword, NewValidPassword, MfaCode: validCode);
        var result = await AuthEndpoints.ChangePassword(
            request,
            BuildHttpContext(),
            userStore,
            tenantAuthConfigStore,
            new AuthEventService(authEventStore),
            notifications,
            CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();
        // Password must have been updated
        user.PasswordHash.Should().NotBe(originalHash);
        PasswordService.VerifyPassword(NewValidPassword, user.PasswordHash!).Should().BeTrue();

        await authEventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e.EventType == AuthEventTypes.PasswordChange),
            Arg.Any<CancellationToken>());
        await notifications.Received(1).CreateAsync(
            TestTenantId,
            "security.password_changed",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─── T4: MFA not enrolled, code absent → 200 (existing behavior) ────────

    [Fact]
    public async Task ChangePassword_ShouldSucceed_WhenUserMfaDisabledAndCodeOmitted()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var notifications = Substitute.For<INotificationService>();

        var user = BuildNoMfaUser();
        var originalHash = user.PasswordHash;

        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(new TenantAuthConfig { TenantId = TestTenantId }));

        var request = new ChangePasswordRequest(TestPassword, NewValidPassword);
        var result = await AuthEndpoints.ChangePassword(
            request,
            BuildHttpContext(),
            userStore,
            tenantAuthConfigStore,
            new AuthEventService(authEventStore),
            notifications,
            CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();
        user.PasswordHash.Should().NotBe(originalHash);
    }

    // ─── T5: Missing MFA code takes priority over wrong old password ─────────

    [Fact]
    public async Task ChangePassword_ShouldReturn401WithStepUp_WhenMfaCodeMissingEvenIfOldPasswordWrong()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var notifications = Substitute.For<INotificationService>();

        var (user, _) = BuildMfaUser();

        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(new TenantAuthConfig { TenantId = TestTenantId }));

        // Both wrong old password AND missing MFA code → must get step-up (not 400 for wrong password)
        var request = new ChangePasswordRequest("WrongOldPassword!", NewValidPassword);
        var result = await AuthEndpoints.ChangePassword(
            request,
            BuildHttpContext(),
            userStore,
            tenantAuthConfigStore,
            new AuthEventService(authEventStore),
            notifications,
            CancellationToken.None);

        // Must be the step-up 401, not a BadRequest about wrong password
        var jsonResult = result as Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<MfaStepUpRequiredResponse>;
        jsonResult.Should().NotBeNull("MFA step-up must fire before old-password check");
        jsonResult!.StatusCode.Should().Be(401);
        jsonResult.Value!.MfaStepUpRequired.Should().BeTrue();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Returns a user with MFA enabled and a real base32 TOTP secret.</summary>
    private static (User User, byte[] SecretBytes) BuildMfaUser()
    {
        var secretBytes = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(secretBytes);
        var user = new User
        {
            UserId = EntityId.From(TestUserId),
            TenantId = new TenantId(TestTenantId),
            Email = "test@example.com",
            DisplayName = "Test User",
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            MfaEnabled = true,
            MfaSecret = secret,
            MfaRecoveryCodes = new[] { "code1", "code2" },
            MfaConfirmedAt = DateTimeOffset.UtcNow,
            PasswordHash = s_knownHash,
        };
        return (user, secretBytes);
    }

    private static User BuildNoMfaUser() => new()
    {
        UserId = EntityId.From(TestUserId),
        TenantId = new TenantId(TestTenantId),
        Email = "test@example.com",
        DisplayName = "Test User",
        Role = UserRole.Agent,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        MfaEnabled = false,
        PasswordHash = s_knownHash,
    };

    private static DefaultHttpContext BuildHttpContext()
    {
        var ctx = new DefaultHttpContext();
        var claims = new List<Claim>
        {
            new("tid", TestTenantId),
            new("sub", TestUserId),
            new("user_id", TestUserId),
        };
        var identity = new ClaimsIdentity(claims, "TestScheme");
        ctx.User = new ClaimsPrincipal(identity);
        return ctx;
    }
}
