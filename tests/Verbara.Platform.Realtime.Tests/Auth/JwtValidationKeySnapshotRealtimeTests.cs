using System.Diagnostics;
using System.Security.Cryptography;
using Verbara.Platform.Identity.Auth.Jwt;
using Verbara.Platform.Realtime.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Realtime.Tests.Auth;

/// <summary>
/// Realtime's token check reads its keys from memory: a key pool that never answers or answers with
/// nothing cannot hold a hub connect or empty the key set.
/// </summary>
public sealed class JwtValidationKeySnapshotRealtimeTests
{
    private sealed class ScriptedKeyStore : IJwtKeyStore
    {
        public bool NeverAnswer { get; set; }
        public bool Empty { get; set; }
        public List<JwtKeyEntry> Entries { get; } = [];

        public async Task<IReadOnlyList<JwtKeyEntry>> GetAllAsync(CancellationToken ct = default)
        {
            if (NeverAnswer)
            {
                await new TaskCompletionSource().Task.ConfigureAwait(false);
                throw new UnreachableException();
            }
            return Empty ? [] : Entries.ToArray();
        }

        public Task<JwtKeyEntry?> GetActiveAsync(CancellationToken ct = default) =>
            Task.FromResult(Entries.FirstOrDefault(e => e.IsActive));

        public Task UpsertAsync(JwtKeyEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RemoveExpiredAsync(DateTimeOffset asOf, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static JwtKeyEntry NewKey(string kid) => new()
    {
        KeyId = kid,
        Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        Algorithm = JwtKeyAlgorithm.Hs256,
        ActivatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        IsActive = true,
    };

    private static ServiceProvider Services(ScriptedKeyStore store, TimeProvider? clock = null) =>
        new ServiceCollection()
            .AddSingleton<IJwtKeyStore>(store)
            .AddSingleton(sp => JwtValidationConfigurator.CreateKeySnapshot(sp, clock))
            .BuildServiceProvider();

    [Fact]
    public async Task IssuerSigningKeyResolver_ShouldReturnWithinMilliseconds_WhenTheKeyStoreNeverAnswers()
    {
        var store = new ScriptedKeyStore { NeverAnswer = true };
        await using var services = Services(store);
        var parameters = JwtValidationConfigurator.BuildValidationParameters(services);

        var resolve = Task.Run(() => parameters.IssuerSigningKeyResolver("token", null, "kid", parameters).ToList());
        var finished = await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(2))); // fence-allow: GUARD-TIMEOUT — bounds the wait on a resolver that may block; the miss is the assertion

        finished.Should().BeSameAs(resolve, because: "a hub connect must never wait on the key pool");
    }

    [Fact]
    public async Task IssuerSigningKeyResolver_ShouldKeepThePreviousKeys_WhenTheStoreAnswersWithNoKeys()
    {
        var store = new ScriptedKeyStore();
        store.Entries.Add(NewKey("k1"));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var services = Services(store, clock);
        var parameters = JwtValidationConfigurator.BuildValidationParameters(services);
        var keys = services.GetRequiredService<JwtValidationKeySnapshot>();

        await keys.EnsureLoadedAsync(CancellationToken.None);
        parameters.IssuerSigningKeyResolver("token", null, "k1", parameters).Select(k => k.KeyId).Should().Contain("k1");

        store.Empty = true;
        clock.Advance(TimeSpan.FromMinutes(6));
        parameters.IssuerSigningKeyResolver("token", null, "k1", parameters).Select(k => k.KeyId).Should().Contain("k1");
        await keys.ReloadInFlight;

        parameters.IssuerSigningKeyResolver("token", null, "k1", parameters).Select(k => k.KeyId)
            .Should().Contain("k1", because: "an empty key set is a failed read, not a pool with no keys");
    }

    [Fact]
    public async Task IssuerSigningKeyResolver_ShouldAcceptTheFirstKey_WhenSetupCreatesItAfterTheStoreWasEmpty()
    {
        // A fresh install: the store holds no key until setup finishes; requests (health probes, the
        // setup screen's live updates) reach Realtime meanwhile.
        var store = new ScriptedKeyStore();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var services = Services(store, clock);
        var parameters = JwtValidationConfigurator.BuildValidationParameters(services);
        var keys = services.GetRequiredService<JwtValidationKeySnapshot>();
        for (var i = 0; i < 6; i++)
        {
            if (i > 0)
                clock.Advance(TimeSpan.FromSeconds(20));
            await keys.EnsureLoadedAsync(null, CancellationToken.None);
            await keys.ReloadInFlight;
        }

        var first = NewKey("first-key");
        await store.UpsertAsync(first);
        var token = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "verbara-platform",
            Audience = "verbara-platform",
            Expires = DateTime.UtcNow.AddMinutes(15),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(first.Key)) { KeyId = first.KeyId },
                SecurityAlgorithms.HmacSha256),
        });

        // What the JwtBearer OnMessageReceived event does, then the token check.
        await keys.EnsureLoadedAsync(JwtKeyId.FromRequest(null, "Bearer " + token), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        var result = await new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler()
            .ValidateTokenAsync(token, parameters);

        result.IsValid.Should().BeTrue(because: "the admin's first live-update connect after setup must not be refused");
    }
}
