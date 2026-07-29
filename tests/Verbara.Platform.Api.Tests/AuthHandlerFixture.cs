using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth;
using Verbara.Platform.Identity.Mfa;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Shared arrangement for tests that invoke the internal <c>AuthEndpoints</c> handlers directly
/// (this project has InternalsVisibleTo) rather than spinning up WebApplicationFactory.
///
/// Extracted from MfaPolicyEnforcementTests, where it began as a private nested class, so the
/// refresh-permission regression tests can reuse the wiring instead of duplicating ~170 lines of
/// JWT / refresh-token / lockout setup. The seed values live here too, and the original test class
/// aliases them, so there is exactly one definition of each.
/// </summary>
internal sealed class AuthHandlerFixture
{

    public const string TenantId = "mfa-policy-tenant";
    public const string UserId = "mfa-policy-user";
    public const string Email = "user@mfa-policy.test";
    public const string Password = "CurrentPassword123!";

    // BCrypt hash of Password — hashed once per class load to avoid BCrypt cost per test.
    internal static readonly string s_passwordHash = PasswordService.HashPassword(Password);
    internal static readonly string[] s_recoveryCodes = ["code1"];
    internal static readonly string[] s_wildcardScope = ["*"];
    public readonly IUserStore UserStore = Substitute.For<IUserStore>();
    public readonly IApiKeyStore ApiKeyStore = Substitute.For<IApiKeyStore>();
    public readonly ITenantAuthConfigStore ConfigStore = Substitute.For<ITenantAuthConfigStore>();
    public readonly IAuthEventStore AuthEventStore = Substitute.For<IAuthEventStore>();
    public readonly IRefreshTokenStore RefreshTokenStore = Substitute.For<IRefreshTokenStore>();
    public readonly Verbara.Platform.Identity.Mfa.IMfaPolicyEvaluator MfaPolicyEvaluator =
        Substitute.For<Verbara.Platform.Identity.Mfa.IMfaPolicyEvaluator>();
    public readonly Verbara.Platform.Identity.Mfa.InMemoryMfaPendingCache MfaPendingCache = new();
    public readonly Verbara.Platform.Identity.Mfa.InMemoryPasswordResetCache PasswordResetCache = new();
    public readonly AuthEventService AuthEvents;
    public readonly JwtTokenService JwtService;
    public readonly RefreshTokenService RefreshService;
    public readonly AccountLockoutService LockoutService;
    private readonly string _tempKeyDir;

    public User User = null!;

    public AuthHandlerFixture()
    {
        AuthEvents = new AuthEventService(AuthEventStore);
        LockoutService = new AccountLockoutService(UserStore, ConfigStore, AuthEvents);

        // JwtTokenService requires a writable data directory to persist its RSA
        // signing key on first run. Use a unique per-fixture temp dir so parallel
        // test runs don't collide.
        _tempKeyDir = Path.Combine(Path.GetTempPath(), "asterisk-mfa-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempKeyDir);
        JwtService = new JwtTokenService(
            _tempKeyDir,
            DataProtectionProvider.Create("Verbara.Platform.Tests"),
            new InMemoryJtiRevocationCache());

        // In-memory refresh-token backing store so rotation round-trips.
        var storedTokens = new System.Collections.Concurrent.ConcurrentDictionary<string, RefreshToken>();
        RefreshTokenStore.SaveAsync(Arg.Do<RefreshToken>(t => storedTokens[t.TokenHash] = t),
            Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        RefreshTokenStore.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedTokens.TryGetValue(ci.Arg<string>(), out var t);
                return Task.FromResult<RefreshToken?>(t);
            });
        RefreshTokenStore.RevokeAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var tokenId = ci.ArgAt<string>(0);
                var match = storedTokens.Values.FirstOrDefault(v => v.TokenId == tokenId);
                if (match is not null) match.RevokedAt = ci.ArgAt<DateTimeOffset>(1);
                return Task.CompletedTask;
            });
        RefreshTokenStore.RevokeAllForUserAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var tenantArg = ci.ArgAt<string>(0);
                var userArg = ci.ArgAt<string>(1);
                var revokedAt = ci.ArgAt<DateTimeOffset>(2);
                foreach (var t in storedTokens.Values.Where(v => v.TenantId == tenantArg && v.UserId == userArg))
                    t.RevokedAt = revokedAt;
                return Task.CompletedTask;
            });

        RefreshService = new RefreshTokenService(RefreshTokenStore);
    }

    public AuthHandlerFixture WithUser(bool mfaEnabled, UserRole role)
    {
        User = new User
        {
            UserId = EntityId.From(UserId),
            TenantId = new TenantId(TenantId),
            Email = Email,
            DisplayName = "Policy Test User",
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            PasswordHash = s_passwordHash,
            MfaEnabled = mfaEnabled,
            MfaSecret = mfaEnabled ? "JBSWY3DPEHPK3PXP" : null,
            MfaRecoveryCodes = mfaEnabled ? s_recoveryCodes : null,
            MfaConfirmedAt = mfaEnabled ? DateTimeOffset.UtcNow : null,
        };

        UserStore.GetByEmailAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(User));
        UserStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(User));
        return this;
    }

    public AuthHandlerFixture WithTenantPolicy(TenantAuthConfig config)
    {
        ConfigStore.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(config));
        // Wire IMfaPolicyEvaluator to delegate to the same config so tests stay coherent.
        MfaPolicyEvaluator.RequiresMfaAsync(TenantId, Arg.Any<UserRole>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(config.IsMfaRequiredForRole(ci.Arg<UserRole>().ToString())));
        return this;
    }

    public AuthHandlerFixture WithApiKey(ApiKeyType keyType, string rawKey)
    {
        var apiKey = new ApiKey
        {
            KeyId = EntityId.From("key-1"),
            TenantId = new TenantId(TenantId),
            Name = "test-key",
            HashedKey = HashKey(rawKey),
            Scopes = s_wildcardScope,
            UserId = EntityId.From(UserId),
            KeyType = keyType,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ApiKeyStore.GetByHashAsync(apiKey.HashedKey, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ApiKey?>(apiKey));
        return this;
    }

    public static DefaultHttpContext BuildHttpContext(string? refreshCookie = null)
    {
        var ctx = new DefaultHttpContext();
        // Minimum DI so the endpoints' RequestServices.GetService<T>() calls don't NRE
        var services = new ServiceCollection();
        ctx.RequestServices = services.BuildServiceProvider();

        var claims = new List<Claim>
        {
            new("tid", TenantId),
            new("sub", UserId),
            new("user_id", UserId),
        };
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));

        if (refreshCookie is not null)
        {
            ctx.Request.Headers.Cookie = $"refresh_token={refreshCookie}";
        }

        return ctx;
    }

    public Task<IResult> InvokeLoginAsync(LoginRequest body) =>
        AuthEndpoints.Login(
            body,
            BuildHttpContext(refreshCookie: null),
            UserStore,
            LockoutService,
            JwtService,
            RefreshService,
            AuthEvents,
            ConfigStore,
            MfaPolicyEvaluator,
            MfaPendingCache,
            authWriteQueue: null, // AHH Phase 4 — null queue → rehash skipped (test path)
            CancellationToken.None);

    public Task<IResult> InvokeApiKeyLoginAsync(ApiKeyLoginRequest body) =>
        AuthEndpoints.ApiKeyLogin(
            body,
            BuildHttpContext(refreshCookie: null),
            ApiKeyStore,
            UserStore,
            ConfigStore,
            MfaPolicyEvaluator,
            JwtService,
            AuthEvents,
            CancellationToken.None);

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}
