using System.Security.Claims;
using System.Text.Json;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Notifications;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OtpNet;

namespace Verbara.Platform.Api.Tests.Endpoints;

public sealed class AuthEndpointsTests
{
    private const string TestTenantId = "tenant1";
    private const string TestUserId = "user1";
    private const string TestPassword = "CurrentPassword123!";

    // BCrypt hash of TestPassword — computed once at class load to avoid rehashing for every test.
    private static readonly string s_knownHash = PasswordService.HashPassword(TestPassword);

    private static readonly string[] s_expectedPasswordPolicyProps =
        ["MinLength", "RequireUppercase", "RequireNumber", "RequireSpecial"];

    /// <summary>
    /// ⚠ NOT recovery-code coverage. These are inert placeholders whose only job is
    /// to make <c>MfaEnabled == true</c> look plausible on the fixture user.
    /// </summary>
    /// <remarks>
    /// They belong to NEITHER stored-digest family: not BCrypt (no <c>$2</c> prefix)
    /// and not a salted SHA-256 hex digest (not 64 hex chars). No test in this file
    /// ever redeems one — and until <c>fix-recovery-code-redemption</c>, redeeming
    /// one would have thrown <c>BCrypt.Net.SaltParseException</c> and surfaced as
    /// HTTP 500 rather than 401. Real mint→redeem coverage across both families
    /// lives in <c>Mfa/MfaRecoveryCodeRedemptionTests</c>; the format-dispatch unit
    /// coverage lives in <c>Services/MfaServiceTests</c>. Do not read the presence
    /// of this array as either.
    /// </remarks>
    private static readonly string[] s_placeholderNonDigestRecoveryCodes = ["code1", "code2"];

    [Fact]
    public void MfaChallengeResponse_ShouldSerializeWithFrontendFieldNames_WhenSerialized()
    {
        var response = new MfaChallengeResponse(true, "abc123");

        var json = JsonSerializer.Serialize(response, ApiJsonContext.Default.MfaChallengeResponse);

        json.Should().Contain("\"requiresMfa\":true");
        json.Should().Contain("\"mfaToken\":\"abc123\"");
    }

    // ─── A1: refresh cookie path + max-age ──────────────────────────────────

    [Fact]
    public void SetRefreshCookie_ShouldScopeCookieToVersionedAuthPath_WhenIssued()
    {
        var ctx = new DefaultHttpContext();

        AuthEndpoints.SetRefreshCookieForTest(ctx, "raw-token-value");

        var setCookie = ctx.Response.Headers.SetCookie.ToString().ToLowerInvariant();
        setCookie.Should().Contain("refresh_token=");
        setCookie.Should().Contain("path=/api/v1/auth");
        setCookie.Should().Contain("max-age=86400");
        setCookie.Should().Contain("httponly");
        setCookie.Should().Contain("samesite=strict");
        setCookie.Should().NotContain("path=/api/auth;");
    }

    // ─── A4: sessionIdleTimeoutMinutes on TokenResponse ─────────────────────

    [Fact]
    public void TokenResponse_ShouldSerializeSessionIdleTimeoutMinutes_WhenSet()
    {
        var resp = new TokenResponse(
            "access-token",
            DateTimeOffset.UtcNow,
            null,
            TestTenantId,
            null,
            null,
            30);

        var json = JsonSerializer.Serialize(resp, ApiJsonContext.Default.TokenResponse);

        json.Should().Contain("\"sessionIdleTimeoutMinutes\":30");
    }

    // ─── A2: refresh cookie delete path ─────────────────────────────────────

    [Fact]
    public async Task Logout_ShouldDeleteCookieOnVersionedAuthPath_WhenCalled()
    {
        var refreshTokenStore = Substitute.For<IRefreshTokenStore>();
        var refreshService = new RefreshTokenService(refreshTokenStore);
        var authEvents = new AuthEventService(Substitute.For<IAuthEventStore>());

        var ctx = BuildHttpContext();
        var result = await AuthEndpoints.Logout(
            ctx, refreshService, authEvents, CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();

        var setCookie = ctx.Response.Headers.SetCookie.ToString().ToLowerInvariant();
        setCookie.Should().Contain("refresh_token=");
        setCookie.Should().Contain("path=/api/v1/auth");
        setCookie.Should().Contain("expires=thu, 01 jan 1970");
    }

    [Fact]
    public async Task Refresh_ShouldDeleteCookieOnVersionedAuthPath_WhenMfaPolicyRevokesSession()
    {
        // Drive the MFA-policy-revoke branch end-to-end: a valid refresh cookie
        // rotates successfully, the user is resolved, but the tenant policy now
        // requires MFA while the user is not enrolled → the whole lineage is
        // revoked and the refresh cookie MUST be deleted on the issuing Path so
        // the browser actually clears it.
        var refreshTokenStore = Substitute.For<IRefreshTokenStore>();
        var refreshService = new RefreshTokenService(refreshTokenStore);
        var userStore = Substitute.For<IUserStore>();
        var configStore = Substitute.For<ITenantAuthConfigStore>();
        var mfaPolicyEvaluator = Substitute.For<IMfaPolicyEvaluator>();
        var authEvents = new AuthEventService(Substitute.For<IAuthEventStore>());

        const string rawCookieToken = "incoming-refresh-token";
        var storedToken = new RefreshToken
        {
            TokenId = "rotated-from",
            UserId = TestUserId,
            TenantId = TestTenantId,
            TokenHash = RefreshTokenService.HashToken(rawCookieToken),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        // RotateAsync resolves the incoming token by hash → a valid, non-revoked token.
        refreshTokenStore.GetByHashAsync(storedToken.TokenHash, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RefreshToken?>(storedToken));

        var user = BuildUser(mfaEnabled: false, role: UserRole.Admin);
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        // Policy now requires MFA for this role; user is not enrolled → revoke branch.
        mfaPolicyEvaluator.RequiresMfaAsync(TestTenantId, Arg.Any<UserRole>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var ctx = BuildHttpContext();
        ctx.Request.Headers.Cookie = $"refresh_token={rawCookieToken}";

        var result = await AuthEndpoints.Refresh(
            ctx, userStore, jwtService: null!, refreshService, refreshTokenStore,
            configStore, mfaPolicyEvaluator, authEvents, CancellationToken.None);

        var statusResult = result.Should().BeOfType<JsonHttpResult<MfaEnrollmentRequiredResponse>>().Subject;
        statusResult.StatusCode.Should().Be(401);

        await refreshTokenStore.Received(1).RevokeAllForUserAsync(
            TestTenantId, TestUserId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        var setCookie = ctx.Response.Headers.SetCookie.ToString().ToLowerInvariant();
        setCookie.Should().Contain("refresh_token=");
        setCookie.Should().Contain("path=/api/v1/auth");
        setCookie.Should().Contain("expires=thu, 01 jan 1970");
    }

    [Fact]
    public async Task MfaDisable_ShouldReturn403_WhenTenantPolicyRequiresMfaAll()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var authEventService = new AuthEventService(authEventStore);
        var notifications = Substitute.For<INotificationService>();

        var user = BuildUser(mfaEnabled: true, role: UserRole.Agent);
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        var authConfig = new TenantAuthConfig
        {
            TenantId = TestTenantId,
            MfaPolicy = "required_all",
        };
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(authConfig));

        var request = new MfaDisableRequest(TestPassword);
        var result = await AuthEndpoints.MfaDisable(
            request, BuildHttpContext(), userStore, tenantAuthConfigStore,
            authEventService, notifications, CancellationToken.None);

        var statusResult = result.Should().BeOfType<JsonHttpResult<ErrorResponse>>().Subject;
        statusResult.StatusCode.Should().Be(403);
        user.MfaEnabled.Should().BeTrue("policy should block disable and leave MFA enabled");
        await notifications.DidNotReceiveWithAnyArgs().CreateAsync(
            default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task MfaDisable_ShouldReturn403_WhenUserRoleIsInMfaRequiredRoles()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var authEventService = new AuthEventService(authEventStore);
        var notifications = Substitute.For<INotificationService>();

        var user = BuildUser(mfaEnabled: true, role: UserRole.Admin);
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        var authConfig = new TenantAuthConfig
        {
            TenantId = TestTenantId,
            MfaPolicy = "required_for_roles",
            MfaRequiredRoles = new[] { "Admin", "Supervisor" },
        };
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(authConfig));

        var request = new MfaDisableRequest(TestPassword);
        var result = await AuthEndpoints.MfaDisable(
            request, BuildHttpContext(), userStore, tenantAuthConfigStore,
            authEventService, notifications, CancellationToken.None);

        var statusResult = result.Should().BeOfType<JsonHttpResult<ErrorResponse>>().Subject;
        statusResult.StatusCode.Should().Be(403);
        user.MfaEnabled.Should().BeTrue("policy should block disable and leave MFA enabled");
        await notifications.DidNotReceiveWithAnyArgs().CreateAsync(
            default!, default!, default!, default!, default, default);
    }

    // ─── Sub C T2.1a: user-scoped sessions management ──────────────────────

    [Fact]
    public async Task GetOwnSessions_ShouldReturnOnlyCurrentUserSessions_WhenCalled()
    {
        var refreshTokenStore = Substitute.For<IRefreshTokenStore>();
        var tokens = new List<RefreshToken>
        {
            new() { TokenId = "t1", UserId = "user1", TenantId = "tenant1", TokenHash = "h1",
                    CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    IpAddress = "1.2.3.4", UserAgent = "Chrome" },
            new() { TokenId = "t2", UserId = "user1", TenantId = "tenant1", TokenHash = "h2",
                    CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    IpAddress = "5.6.7.8", UserAgent = "Firefox" },
        };
        refreshTokenStore.GetActiveByUserAsync("tenant1", "user1", Arg.Any<CancellationToken>())
            .Returns(tokens);

        var result = await AuthEndpoints.GetOwnSessions(
            BuildHttpContext(),
            refreshTokenStore,
            CancellationToken.None);

        result.Should().BeOfType<Ok<UserSessionDto[]>>();
        var dtos = ((Ok<UserSessionDto[]>)result).Value!;
        dtos.Should().HaveCount(2);
        dtos.Select(d => d.TokenId).Should().BeEquivalentTo(["t1", "t2"]);
    }

    [Fact]
    public void GetOwnSessions_ShouldIgnoreUserIdQueryParam_WhenProvided()
    {
        // The endpoint signature must NOT have a userId parameter; claims are the only source.
        var method = typeof(AuthEndpoints).GetMethod("GetOwnSessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? typeof(AuthEndpoints).GetMethod("GetOwnSessions",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.GetParameters().Select(p => p.Name).Should().NotContain("userId");
    }

    [Fact]
    public async Task RevokeOwnSession_ShouldReturn404_WhenTokenBelongsToOtherUser()
    {
        var refreshTokenStore = Substitute.For<IRefreshTokenStore>();
        // Current user has no sessions with tokenId "t1"
        refreshTokenStore.GetActiveByUserAsync("tenant1", "user1", Arg.Any<CancellationToken>())
            .Returns(new List<RefreshToken>());

        var result = await AuthEndpoints.RevokeOwnSession(
            "t1",
            BuildHttpContext(),
            refreshTokenStore,
            CancellationToken.None);

        result.Should().BeOfType<NotFound>();
        await refreshTokenStore.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default, default, default);
    }

    [Fact]
    public async Task RevokeOwnSession_ShouldSucceed_WhenTokenBelongsToCurrentUser()
    {
        var refreshTokenStore = Substitute.For<IRefreshTokenStore>();
        var ownToken = new RefreshToken
        {
            TokenId = "t1", UserId = "user1", TenantId = "tenant1",
            TokenHash = "h1", CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        refreshTokenStore.GetActiveByUserAsync("tenant1", "user1", Arg.Any<CancellationToken>())
            .Returns(new List<RefreshToken> { ownToken });

        var result = await AuthEndpoints.RevokeOwnSession(
            "t1",
            BuildHttpContext(),
            refreshTokenStore,
            CancellationToken.None);

        result.Should().BeOfType<Ok>();
        await refreshTokenStore.Received(1).RevokeAsync("t1", Arg.Any<DateTimeOffset>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeOtherSessions_ShouldPreserveCurrentSession_WhenCalled()
    {
        var refreshTokenStore = Substitute.For<IRefreshTokenStore>();
        var tokens = new List<RefreshToken>
        {
            new() { TokenId = "current", UserId = "user1", TenantId = "tenant1", TokenHash = "hash-current",
                    CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) },
            new() { TokenId = "other-1", UserId = "user1", TenantId = "tenant1", TokenHash = "hash-other-1",
                    CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) },
            new() { TokenId = "other-2", UserId = "user1", TenantId = "tenant1", TokenHash = "hash-other-2",
                    CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) },
        };
        refreshTokenStore.GetActiveByUserAsync("tenant1", "user1", Arg.Any<CancellationToken>())
            .Returns(tokens);
        // Simulate current session: GetByHashAsync returns the "current" token when asked
        refreshTokenStore.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(tokens[0]);

        // Build context WITH refresh_token cookie so the handler can detect current session
        var ctx = BuildHttpContext();
        ctx.Request.Headers.Cookie = "refresh_token=some-plaintext-token";

        var result = await AuthEndpoints.RevokeOtherSessions(
            ctx,
            refreshTokenStore,
            CancellationToken.None);

        result.Should().BeOfType<Ok>();
        await refreshTokenStore.DidNotReceive().RevokeAsync("current", Arg.Any<DateTimeOffset>(), null, Arg.Any<CancellationToken>());
        await refreshTokenStore.Received(1).RevokeAsync("other-1", Arg.Any<DateTimeOffset>(), null, Arg.Any<CancellationToken>());
        await refreshTokenStore.Received(1).RevokeAsync("other-2", Arg.Any<DateTimeOffset>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MfaDisable_ShouldSucceed_WhenTenantPolicyIsOptional()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var authEventService = new AuthEventService(authEventStore);
        var notifications = Substitute.For<INotificationService>();

        var user = BuildUser(mfaEnabled: true, role: UserRole.Agent);
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        var authConfig = new TenantAuthConfig
        {
            TenantId = TestTenantId,
            MfaPolicy = "optional",
        };
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(authConfig));

        var request = new MfaDisableRequest(TestPassword);
        var result = await AuthEndpoints.MfaDisable(
            request, BuildHttpContext(), userStore, tenantAuthConfigStore,
            authEventService, notifications, CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();
        user.MfaEnabled.Should().BeFalse();
        user.MfaSecret.Should().BeNull();
        user.MfaRecoveryCodes.Should().BeNull();
        user.MfaConfirmedAt.Should().BeNull();
    }

    // ─── Sub C T2.4a: security notification emits ──────────────────────────

    [Fact]
    public async Task MfaConfirm_ShouldEmitNotification_WhenSucceeds()
    {
        var userStore = Substitute.For<IUserStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var notifications = Substitute.For<INotificationService>();

        // Use a real base32 secret so MfaService.VerifyCode accepts the TOTP we compute.
        var secretBytes = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(secretBytes);

        var user = BuildUser(mfaEnabled: false);
        user.MfaSecret = secret;
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        var validCode = new Totp(secretBytes).ComputeTotp(DateTime.UtcNow);

        var request = new MfaConfirmRequest(validCode);
        var result = await AuthEndpoints.MfaConfirm(
            request,
            BuildHttpContext(),
            userStore,
            new AuthEventService(authEventStore),
            notifications,
            CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();
        user.MfaEnabled.Should().BeTrue();
        await notifications.Received(1).CreateAsync(
            TestTenantId,
            "security.mfa_enabled",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MfaDisable_ShouldEmitNotification_WhenSucceeds()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var notifications = Substitute.For<INotificationService>();

        var user = BuildUser(mfaEnabled: true, role: UserRole.Agent);
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        var authConfig = new TenantAuthConfig { TenantId = TestTenantId, MfaPolicy = "optional" };
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(authConfig));

        var request = new MfaDisableRequest(TestPassword);
        var result = await AuthEndpoints.MfaDisable(
            request,
            BuildHttpContext(),
            userStore,
            tenantAuthConfigStore,
            new AuthEventService(authEventStore),
            notifications,
            CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();
        await notifications.Received(1).CreateAsync(
            TestTenantId,
            "security.mfa_disabled",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangePassword_ShouldEmitNotification_WhenSucceeds()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var notifications = Substitute.For<INotificationService>();

        var user = BuildUser();
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(new TenantAuthConfig { TenantId = TestTenantId }));

        var request = new ChangePasswordRequest(TestPassword, "NewValidPassword456!");
        var result = await AuthEndpoints.ChangePassword(
            request,
            BuildHttpContext(),
            userStore,
            tenantAuthConfigStore,
            new AuthEventService(authEventStore),
            notifications,
            CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();
        await notifications.Received(1).CreateAsync(
            TestTenantId,
            "security.password_changed",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─── Sub C T2.2a: recovery codes regenerate ─────────────────────────────

    [Fact]
    public async Task RegenerateRecoveryCodes_ShouldReturn10Codes_WhenMfaEnabledAndPasswordCorrect()
    {
        var userStore = Substitute.For<IUserStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var user = BuildUser(mfaEnabled: true);
        var originalCodes = new[] { "old1", "old2" };
        user.MfaRecoveryCodes = originalCodes;
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        var request = new RegenerateRecoveryCodesRequest(TestPassword);

        var result = await AuthEndpoints.RegenerateRecoveryCodes(
            request,
            BuildHttpContext(),
            userStore,
            new AuthEventService(authEventStore),
            CancellationToken.None);

        result.Should().BeOfType<Ok<RecoveryCodesResponse>>();
        var response = ((Ok<RecoveryCodesResponse>)result).Value!;
        response.RecoveryCodes.Should().HaveCount(10);
        user.MfaRecoveryCodes.Should().NotBeNull();
        user.MfaRecoveryCodes!.Should().NotBeSameAs(originalCodes);
        user.MfaRecoveryCodes.Should().NotContain("old1");
        user.MfaRecoveryCodes.Should().NotContain("old2");
        await authEventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e != null && e.EventType == AuthEventTypes.RecoveryCodesRegenerated),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegenerateRecoveryCodes_ShouldReturn400_WhenMfaNotEnabled()
    {
        var userStore = Substitute.For<IUserStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var user = BuildUser(mfaEnabled: false);
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        var request = new RegenerateRecoveryCodesRequest(TestPassword);

        var result = await AuthEndpoints.RegenerateRecoveryCodes(
            request,
            BuildHttpContext(),
            userStore,
            new AuthEventService(authEventStore),
            CancellationToken.None);

        result.Should().BeOfType<BadRequest<ErrorResponse>>();
        await authEventStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    // ─── Sub C T2.3a: password policy endpoint ─────────────────────────────

    [Fact]
    public async Task GetPasswordPolicy_ShouldReturnTenantPolicy_WhenCalled()
    {
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authConfig = new TenantAuthConfig
        {
            TenantId = TestTenantId,
            PasswordMinLength = 14,
            PasswordRequireUppercase = true,
            PasswordRequireNumber = true,
            PasswordRequireSpecial = false,
        };
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(authConfig));

        var result = await AuthEndpoints.GetPasswordPolicy(
            BuildHttpContext(),
            tenantAuthConfigStore,
            CancellationToken.None);

        result.Should().BeOfType<Ok<PasswordPolicyDto>>();
        var dto = ((Ok<PasswordPolicyDto>)result).Value!;
        dto.MinLength.Should().Be(14);
        dto.RequireUppercase.Should().BeTrue();
        dto.RequireNumber.Should().BeTrue();
        dto.RequireSpecial.Should().BeFalse();
    }

    [Fact]
    public void GetPasswordPolicy_ShouldNotLeakSecrets_WhenCalled()
    {
        // Verify the DTO shape via reflection — ensure only the 4 password-related
        // properties exist (no OIDC secrets, no lockout config, no MFA policy, etc.)
        var props = typeof(PasswordPolicyDto).GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        props.Should().BeEquivalentTo(s_expectedPasswordPolicyProps);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static User BuildUser(bool mfaEnabled = false, UserRole role = UserRole.Agent)
    {
        return new User
        {
            UserId = EntityId.From(TestUserId),
            TenantId = new TenantId(TestTenantId),
            Email = "test@example.com",
            DisplayName = "Test User",
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            MfaEnabled = mfaEnabled,
            MfaSecret = mfaEnabled ? "SECRET" : null,
            // See the field's remarks: placeholders, NOT redemption coverage.
            MfaRecoveryCodes = mfaEnabled ? s_placeholderNonDigestRecoveryCodes : null,
            MfaConfirmedAt = mfaEnabled ? DateTimeOffset.UtcNow : null,
            PasswordHash = s_knownHash,
        };
    }

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
