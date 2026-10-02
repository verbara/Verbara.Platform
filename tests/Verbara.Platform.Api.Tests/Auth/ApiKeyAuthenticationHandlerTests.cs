using System.Globalization;
using System.Text.Encodings.Web;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="ApiKeyAuthenticationHandler"/>.
/// </summary>
public sealed class ApiKeyAuthenticationHandlerTests
{
    private static ApiKeyAuthenticationHandler CreateHandler(
        IApiKeyStore apiKeyStore,
        IUserStore userStore,
        HttpContext context,
        TimeProvider? time = null)
    {
        var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions { TimeProvider = time });
        options.CurrentValue.Returns(new AuthenticationSchemeOptions { TimeProvider = time });

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var handler = new ApiKeyAuthenticationHandler(
            options,
            loggerFactory,
            UrlEncoder.Default,
            apiKeyStore,
            userStore);

        var scheme = new AuthenticationScheme(
            AuthSchemeConfiguration.ApiKeyScheme,
            AuthSchemeConfiguration.ApiKeyScheme,
            typeof(ApiKeyAuthenticationHandler));

        handler.InitializeAsync(scheme, context).GetAwaiter().GetResult();
        return handler;
    }

    private static DefaultHttpContext CreateContext(string? authHeader = null, string? queryTokenParam = null)
    {
        var context = new DefaultHttpContext();

        if (authHeader is not null)
            context.Request.Headers["Authorization"] = authHeader;

        if (queryTokenParam is not null)
            context.Request.QueryString = new QueryString($"?token={Uri.EscapeDataString(queryTokenParam)}");

        return context;
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldReturnNoResult_WhenApiKeyInQueryStringOnly()
    {
        // Arrange: provide a raw API key value in ?token= with no Authorization header
        var apiKeyStore = Substitute.For<IApiKeyStore>();
        var userStore = Substitute.For<IUserStore>();

        // Even if a matching key existed in the store, the handler must not consult it
        // because the query-string fallback has been removed.
        var context = CreateContext(authHeader: null, queryTokenParam: "some-api-key-value");
        var handler = CreateHandler(apiKeyStore, userStore, context);

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert: no Authorization header → NoResult (store never consulted)
        result.None.Should().BeTrue("the ?token= query parameter must not be used for API key auth");
        await apiKeyStore.DidNotReceive().GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldReturnNoResult_WhenNoHeaderAndNoQuery()
    {
        var apiKeyStore = Substitute.For<IApiKeyStore>();
        var userStore = Substitute.For<IUserStore>();
        var context = CreateContext();
        var handler = CreateHandler(apiKeyStore, userStore, context);

        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldReturnFailure_WhenBearerKeyIsInvalid()
    {
        var apiKeyStore = Substitute.For<IApiKeyStore>();
        apiKeyStore.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ApiKey?)null);
        var userStore = Substitute.For<IUserStore>();

        var context = CreateContext(authHeader: "Bearer invalid-key");
        var handler = CreateHandler(apiKeyStore, userStore, context);

        var result = await handler.AuthenticateAsync();

        result.Failure.Should().NotBeNull();
    }

    // ─── Key owner's account status ──────────────────────────────────────────
    // A user-bound key acts as its owner, so it authenticates only while the owner may: the
    // same account-status rule the sign-in endpoints apply, checked on every request (the
    // handler already loads the owner to emit its role).

    private const string OwnerTenantId = "key-owner-tenant";
    private const string OwnerUserId = "key-owner-user";
    private const string RawOwnedKey = "owned-raw-key";

    public static TheoryData<UserStatus> InactiveStatuses => new()
    {
        UserStatus.Suspended,
        UserStatus.Deactivated,
    };

    [Theory]
    [MemberData(nameof(InactiveStatuses))]
    public async Task HandleAuthenticate_ShouldFail_WhenKeyOwnerIsNotActive(UserStatus status)
    {
        var (apiKeyStore, userStore) = StoresWithOwnedKey(ApiKeyType.Standard, OwnerWith(status));
        var handler = CreateHandler(apiKeyStore, userStore, CreateContext($"Bearer {RawOwnedKey}"));

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse(
            because: "a key bound to a suspended or deactivated user must stop authenticating");
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldFail_WhenManagementKeyOwnerIsSuspended()
    {
        // Management keys are exempt from the MFA policy (they authenticate a workload), but a
        // management key bound to a person still acts as that person.
        var (apiKeyStore, userStore) = StoresWithOwnedKey(ApiKeyType.Management, OwnerWith(UserStatus.Suspended));
        var handler = CreateHandler(apiKeyStore, userStore, CreateContext($"Bearer {RawOwnedKey}"));

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldFail_WhenKeyOwnerNoLongerExists()
    {
        var (apiKeyStore, userStore) = StoresWithOwnedKey(ApiKeyType.Standard, owner: null);
        var handler = CreateHandler(apiKeyStore, userStore, CreateContext($"Bearer {RawOwnedKey}"));

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse(
            because: "a key bound to a deleted user has no owner to act as — it must not authenticate "
                   + "as an owner-less principal");
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldSucceedWithOwnerClaims_WhenKeyOwnerIsActive()
    {
        var (apiKeyStore, userStore) = StoresWithOwnedKey(ApiKeyType.Standard, OwnerWith(UserStatus.Active));
        var handler = CreateHandler(apiKeyStore, userStore, CreateContext($"Bearer {RawOwnedKey}"));

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst("user_id")!.Value.Should().Be(OwnerUserId);
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldSucceed_WhenManagementKeyHasNoOwner()
    {
        var apiKeyStore = Substitute.For<IApiKeyStore>();
        apiKeyStore.GetByHashAsync(HashKey(RawOwnedKey), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ApiKey?>(NewKey(ApiKeyType.Management, ownerId: null)));
        var userStore = Substitute.For<IUserStore>();
        var handler = CreateHandler(apiKeyStore, userStore, CreateContext($"Bearer {RawOwnedKey}"));

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue(because: "an owner-less management key has no account status to check");
        await userStore.DidNotReceive().GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>());
    }

    // ─── Expiry of the authentication ────────────────────────────────────────
    // A key is checked on every request, so it carries no expiry a live connection (the event
    // stream) could be bounded by. The handler stamps one, as an access token carries one: an
    // access token's lifetime from now, or the key's own expiry when that comes first.

    // Far from the real clock on purpose: the handler must read the clock it is given.
    private static readonly DateTimeOffset Now = new(2099, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAuthenticate_ShouldStampTheAccessTokenLifetimeAsExpiry_WhenTheKeyNeverExpires()
    {
        var principal = await AuthenticateOwnerlessKeyAsync(keyExpiresAt: null);

        ExpiryOf(principal).Should().Be(Now + TimeSpan.FromMinutes(15),
            because: "a live connection opened with the key must re-authenticate as often as an access token expires");
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldStampTheKeysOwnExpiry_WhenItComesBeforeTheAccessTokenLifetime()
    {
        var principal = await AuthenticateOwnerlessKeyAsync(keyExpiresAt: Now + TimeSpan.FromMinutes(5));

        ExpiryOf(principal).Should().Be(Now + TimeSpan.FromMinutes(5),
            because: "nothing opened with the key may outlive the key");
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldStampTheAccessTokenLifetimeAsExpiry_WhenTheKeyExpiresLater()
    {
        var principal = await AuthenticateOwnerlessKeyAsync(keyExpiresAt: Now + TimeSpan.FromDays(30));

        ExpiryOf(principal).Should().Be(Now + TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldFail_WhenTheKeyHasExpiredByTheHandlersClock()
    {
        // The expiry check and the stamped expiry read one clock, so they cannot disagree.
        var apiKeyStore = Substitute.For<IApiKeyStore>();
        var key = NewKey(ApiKeyType.Standard, ownerId: null);
        key.ExpiresAt = Now;
        apiKeyStore.GetByHashAsync(HashKey(RawOwnedKey), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ApiKey?>(key));
        var handler = CreateHandler(
            apiKeyStore, Substitute.For<IUserStore>(), CreateContext($"Bearer {RawOwnedKey}"), new FakeTimeProvider(Now));

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse(because: "on the handler's clock the key expires exactly now");
    }

    private static async Task<System.Security.Claims.ClaimsPrincipal> AuthenticateOwnerlessKeyAsync(DateTimeOffset? keyExpiresAt)
    {
        var apiKeyStore = Substitute.For<IApiKeyStore>();
        var key = NewKey(ApiKeyType.Standard, ownerId: null);
        key.ExpiresAt = keyExpiresAt;
        apiKeyStore.GetByHashAsync(HashKey(RawOwnedKey), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ApiKey?>(key));
        var handler = CreateHandler(
            apiKeyStore, Substitute.For<IUserStore>(), CreateContext($"Bearer {RawOwnedKey}"), new FakeTimeProvider(Now));

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        return result.Principal!;
    }

    private static DateTimeOffset? ExpiryOf(System.Security.Claims.ClaimsPrincipal principal) =>
        principal.FindFirst("exp")?.Value is { } exp
            ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(exp, CultureInfo.InvariantCulture))
            : null;

    private static (IApiKeyStore ApiKeys, IUserStore Users) StoresWithOwnedKey(ApiKeyType keyType, User? owner)
    {
        var apiKeyStore = Substitute.For<IApiKeyStore>();
        apiKeyStore.GetByHashAsync(HashKey(RawOwnedKey), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ApiKey?>(NewKey(keyType, EntityId.From(OwnerUserId))));

        var userStore = Substitute.For<IUserStore>();
        userStore.GetByIdAsync(new TenantId(OwnerTenantId), EntityId.From(OwnerUserId), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(owner));
        return (apiKeyStore, userStore);
    }

    private static ApiKey NewKey(ApiKeyType keyType, EntityId? ownerId) => new()
    {
        KeyId = EntityId.From("owned-key-id"),
        TenantId = new TenantId(OwnerTenantId),
        Name = "owned key",
        HashedKey = HashKey(RawOwnedKey),
        Scopes = ["*"],
        UserId = ownerId,
        KeyType = keyType,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static User OwnerWith(UserStatus status) => new()
    {
        UserId = EntityId.From(OwnerUserId),
        TenantId = new TenantId(OwnerTenantId),
        Email = "owner@key.test",
        DisplayName = "Key Owner",
        Role = UserRole.Agent,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static string HashKey(string rawKey) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey)));
}
