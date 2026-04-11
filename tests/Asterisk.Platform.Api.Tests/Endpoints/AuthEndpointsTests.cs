using System.Security.Claims;
using System.Text.Json;
using Asterisk.Platform.Api.Endpoints;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests.Endpoints;

public sealed class AuthEndpointsTests
{
    private const string TestTenantId = "tenant1";
    private const string TestUserId = "user1";
    private const string TestPassword = "CurrentPassword123!";

    // BCrypt hash of TestPassword — computed once at class load to avoid rehashing for every test.
    private static readonly string s_knownHash = PasswordService.HashPassword(TestPassword);

    [Fact]
    public void MfaChallengeResponse_ShouldSerializeWithFrontendFieldNames_WhenSerialized()
    {
        var response = new MfaChallengeResponse(true, "abc123");

        var json = JsonSerializer.Serialize(response, ApiJsonContext.Default.MfaChallengeResponse);

        json.Should().Contain("\"requiresMfa\":true");
        json.Should().Contain("\"mfaToken\":\"abc123\"");
    }

    [Fact]
    public async Task MfaDisable_ShouldReturn403_WhenTenantPolicyRequiresMfaAll()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var authEventService = new AuthEventService(authEventStore);

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
            authEventService, CancellationToken.None);

        var statusResult = result.Should().BeOfType<JsonHttpResult<ErrorResponse>>().Subject;
        statusResult.StatusCode.Should().Be(403);
        user.MfaEnabled.Should().BeTrue("policy should block disable and leave MFA enabled");
    }

    [Fact]
    public async Task MfaDisable_ShouldReturn403_WhenUserRoleIsInMfaRequiredRoles()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var authEventService = new AuthEventService(authEventStore);

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
            authEventService, CancellationToken.None);

        var statusResult = result.Should().BeOfType<JsonHttpResult<ErrorResponse>>().Subject;
        statusResult.StatusCode.Should().Be(403);
        user.MfaEnabled.Should().BeTrue("policy should block disable and leave MFA enabled");
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
            authEventService, CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();
        user.MfaEnabled.Should().BeFalse();
        user.MfaSecret.Should().BeNull();
        user.MfaRecoveryCodes.Should().BeNull();
        user.MfaConfirmedAt.Should().BeNull();
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
            Arg.Is<AuthEvent>(e => e.EventType == AuthEventTypes.RecoveryCodesRegenerated),
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
            MfaRecoveryCodes = mfaEnabled ? new[] { "code1", "code2" } : null,
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
