using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// A token check never waits on the key pool: the resolver reads an in-memory snapshot, one reload
/// runs at a time off the request path, a failed or empty reload keeps the held keys, and a signing
/// refresh adds its key instead of clearing the snapshot.
/// </summary>
/// <remarks>
/// Runs alone (<see cref="ThreadPoolSensitiveTests"/>): the starvation case lowers the thread
/// pool's minimum, which would slow every test running beside it.
/// </remarks>
[Collection(ThreadPoolSensitiveTests.Name)]
public sealed class JwtKeySnapshotRegressionTests
{
    private static User MakeUser() => new()
    {
        UserId = EntityId.From("user1"),
        TenantId = new TenantId("t1"),
        Email = "admin@example.com",
        DisplayName = "Admin User",
        Role = UserRole.Admin,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static JwtKeyRotationService NewRotationService(IJwtKeyStore store) => new(
        store,
        TimeProvider.System,
        Options.Create(new JwtKeyRotationOptions
        {
            KeySizeBytes = 32,
            ActiveDuration = TimeSpan.FromHours(1),
            GracePeriod = TimeSpan.FromHours(2),
        }));

    private enum ValidationMode { Pass, NeverAnswer, Throw, Empty, SlowThenThrow }

    private sealed class ScriptedRotationService(IJwtKeyRotationService inner) : IJwtKeyRotationService
    {
        private int _validationCalls;
        public ValidationMode Validation { get; set; }
        public int ValidationCalls => Volatile.Read(ref _validationCalls);

        public Task<JwtKeyEntry> GetActiveSigningKeyAsync(CancellationToken ct = default) =>
            inner.GetActiveSigningKeyAsync(ct);

        public async Task<IReadOnlyList<JwtKeyEntry>> GetValidationKeysAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _validationCalls);
            switch (Validation)
            {
                case ValidationMode.NeverAnswer:
                    await new TaskCompletionSource().Task.ConfigureAwait(false);
                    throw new UnreachableException();
                case ValidationMode.Throw:
                    throw new InvalidOperationException("simulated Redis timeout");
                case ValidationMode.Empty:
                    return [];
                case ValidationMode.SlowThenThrow:
                    await Task.Delay(200, ct).ConfigureAwait(false); // fence-allow: SIMULATED-WORK — a slow Redis read, so many requests arrive while it runs
                    throw new InvalidOperationException("simulated Redis timeout");
                default:
                    return await inner.GetValidationKeysAsync(ct).ConfigureAwait(false);
            }
        }

        public Task<JwtKeyEntry> RotateAsync(CancellationToken ct = default) => inner.RotateAsync(ct);
    }

    private static readonly TimeSpan PastTheKeyCache = TimeSpan.FromMinutes(6);

    private static string MintWith(JwtKeyEntry entry)
    {
        var key = new SymmetricSecurityKey(Convert.FromBase64String(entry.Key)) { KeyId = entry.KeyId };
        return new JwtSecurityTokenHandler().CreateEncodedJwt(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, "user1")]),
            Expires = DateTime.UtcNow.AddMinutes(15),
            Issuer = "verbara-platform",
            Audience = "verbara-platform",
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }

    [Fact]
    public async Task IssuerSigningKeyResolver_ShouldReturnWithinMilliseconds_WhenTheKeyServiceNeverAnswers()
    {
        using var real = NewRotationService(new InMemoryJwtKeyStore());
        _ = await real.RotateAsync();
        var scripted = new ScriptedRotationService(real) { Validation = ValidationMode.NeverAnswer };
        var sut = new JwtTokenService(scripted);
        var resolver = sut.ValidationParameters.IssuerSigningKeyResolver;

        var resolve = Task.Run(() => resolver("token", null, "some-kid", sut.ValidationParameters).ToList());
        var finished = await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(2))); // fence-allow: GUARD-TIMEOUT — bounds the wait on a resolver that may block; the miss is the assertion

        finished.Should().BeSameAs(resolve, because: "the resolver runs inside a request and must never wait on the key pool");
    }

    [Fact]
    public async Task ValidateToken_ShouldStillAccept_WhenASigningRefreshIsFollowedByAFailedValidationReload()
    {
        var store = new InMemoryJwtKeyStore();
        using var real = NewRotationService(store);
        _ = await real.RotateAsync();
        var scripted = new ScriptedRotationService(real);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = new JwtTokenService(scripted, timeProvider: clock);

        var (first, _) = await sut.GenerateAccessTokenAsync(MakeUser());
        (await TokenValidation.ValidateAsync(sut, first)).Should().NotBeNull();

        // The signing key ages out and the pool rotates; the next sign-in refreshes it.
        clock.Advance(PastTheKeyCache);
        _ = await real.RotateAsync();
        _ = await sut.GenerateAccessTokenAsync(MakeUser());
        await sut.SigningKeyRefreshInFlight;
        var (second, _) = await sut.GenerateAccessTokenAsync(MakeUser());
        new JwtSecurityTokenHandler().ReadJwtToken(second).Header.Kid
            .Should().NotBe(new JwtSecurityTokenHandler().ReadJwtToken(first).Header.Kid);

        // Then the validation-key reload fails.
        scripted.Validation = ValidationMode.Throw;
        _ = await TokenValidation.ValidateAsync(sut, first);
        await sut.ValidationKeyReloadInFlight;

        (await TokenValidation.ValidateAsync(sut, first)).Should().NotBeNull(because: "a signing refresh must not drop the keys validation holds");
        (await TokenValidation.ValidateAsync(sut, second)).Should().NotBeNull(because: "the key the service just signed with must validate");
    }

    [Fact]
    public async Task ValidateToken_ShouldKeepThePreviousKeys_WhenAReloadReturnsAnEmptySet()
    {
        using var real = NewRotationService(new InMemoryJwtKeyStore());
        _ = await real.RotateAsync();
        var scripted = new ScriptedRotationService(real);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = new JwtTokenService(scripted, timeProvider: clock);

        var (token, _) = await sut.GenerateAccessTokenAsync(MakeUser());
        (await TokenValidation.ValidateAsync(sut, token)).Should().NotBeNull();

        clock.Advance(PastTheKeyCache);
        scripted.Validation = ValidationMode.Empty;
        (await TokenValidation.ValidateAsync(sut, token)).Should().NotBeNull(because: "the held keys serve while the reload runs");
        await sut.ValidationKeyReloadInFlight;
        scripted.ValidationCalls.Should().Be(2, because: "the reload ran and answered with no keys");

        (await TokenValidation.ValidateAsync(sut, token)).Should().NotBeNull(because: "an empty key set is a failed reload, never cached");
    }

    [Fact]
    public async Task IssuerSigningKeyResolver_ShouldReloadOnce_WhenManyTokensCarryAnUnknownKid()
    {
        var store = new InMemoryJwtKeyStore();
        using var real = NewRotationService(store);
        _ = await real.RotateAsync();
        var scripted = new ScriptedRotationService(real);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = new JwtTokenService(scripted, timeProvider: clock);

        var (token, _) = await sut.GenerateAccessTokenAsync(MakeUser());
        (await TokenValidation.ValidateAsync(sut, token)).Should().NotBeNull();
        var callsWhenWarm = scripted.ValidationCalls;

        // Another replica rotates the pool; its tokens carry a kid this replica has not loaded.
        var rotated = await real.RotateAsync();
        var foreign = MintWith(rotated);

        var clockWatch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => TokenValidation.ValidateAsync(sut, foreign))));
        clockWatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), because: "an unknown kid fails fast, it does not wait on the reload");
        await sut.ValidationKeyReloadInFlight;

        (scripted.ValidationCalls - callsWhenWarm).Should().Be(1, because: "an unknown kid triggers one rate-limited reload, not one per request");
        (await TokenValidation.ValidateAsync(sut, foreign)).Should().NotBeNull(because: "the reload brought the new key in");

        // Another unknown kid inside the rate-limit window starts no reload.
        var stranger = MintWith(rotated with
        {
            KeyId = "never-in-the-pool",
            Key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        });
        for (var i = 0; i < 5; i++)
            (await TokenValidation.ValidateAsync(sut, stranger)).Should().BeNull();
        await sut.ValidationKeyReloadInFlight;
        (scripted.ValidationCalls - callsWhenWarm).Should().Be(1);
    }

    [Fact]
    public async Task ValidateToken_ShouldNotStarveTheThreadPool_WhenManyRequestsCrossAKeyExpiryAndTheReloadIsSlow()
    {
        using var real = NewRotationService(new InMemoryJwtKeyStore());
        _ = await real.RotateAsync();
        var scripted = new ScriptedRotationService(real);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = new JwtTokenService(scripted, timeProvider: clock);

        var (token, _) = await sut.GenerateAccessTokenAsync(MakeUser());
        (await TokenValidation.ValidateAsync(sut, token)).Should().NotBeNull();
        var callsWhenWarm = scripted.ValidationCalls;

        clock.Advance(PastTheKeyCache);
        scripted.Validation = ValidationMode.SlowThenThrow;

        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        ThreadPool.SetMinThreads(2, minIo);
        try
        {
            const int Requests = 64;
            var elapsed = Stopwatch.StartNew();
            var results = await Task.WhenAll(Enumerable.Range(0, Requests)
                .Select(_ => Task.Run(() => TokenValidation.ValidateAsync(sut, token))));
            elapsed.Stop();

            results.Should().AllSatisfy(p => p.Should().NotBeNull());
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
                because: "requests across a key expiry are served from the snapshot while one reload runs");
            await sut.ValidationKeyReloadInFlight;
            (scripted.ValidationCalls - callsWhenWarm).Should().Be(1, because: "one reload at a time, not one per waiting request");
        }
        finally
        {
            ThreadPool.SetMinThreads(minWorkers, minIo);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolSensitiveTests
{
    public const string Name = "Thread-pool sensitive";
}
