using Asterisk.Platform.Identity.Auth.Jwt;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Identity.Tests.Auth.Jwt;

/// <summary>
/// R5.4 S5.9 — unit coverage for the in-process rotation logic. Exercises the
/// invariants documented on <see cref="IJwtKeyRotationService"/>:
/// <list type="bullet">
///   <item>Each <c>RotateAsync</c> mints distinct material + demotes the prior active key.</item>
///   <item>Validation pool returns active + grace-window keys.</item>
///   <item>Expired keys drop out of the validation pool.</item>
///   <item><c>GetActiveSigningKeyAsync</c> bootstraps when the store is empty.</item>
/// </list>
/// </summary>
public sealed class JwtKeyRotationServiceTests : IDisposable
{
    private readonly InMemoryJwtKeyStore _store;
    private readonly TestTimeProvider _time;
    private readonly JwtKeyRotationService _sut;

    public JwtKeyRotationServiceTests()
    {
        _store = new InMemoryJwtKeyStore();
        _time = new TestTimeProvider(DateTimeOffset.Parse("2026-04-26T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var opts = Options.Create(new JwtKeyRotationOptions());
        _sut = new JwtKeyRotationService(_store, _time, opts);
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task RotateAsync_ShouldCreateNewActiveKey_WhenInvoked()
    {
        var first = await _sut.RotateAsync();

        first.IsActive.Should().BeTrue();
        first.KeyId.Should().NotBeNullOrEmpty();

        var second = await _sut.RotateAsync();
        second.IsActive.Should().BeTrue();

        var all = await _store.GetAllAsync();
        all.Should().HaveCount(2);
        all.Single(e => e.KeyId == first.KeyId).IsActive.Should()
            .BeFalse(because: "rotation must demote the previously-active key");
    }

    [Fact]
    public async Task GetValidationKeysAsync_ShouldIncludeBothActiveAndGracePeriodKeys()
    {
        await _sut.RotateAsync();
        await _sut.RotateAsync();

        var validation = await _sut.GetValidationKeysAsync();
        validation.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetValidationKeysAsync_ShouldExcludeExpiredKeys()
    {
        var first = await _sut.RotateAsync();
        // Default lifetime is ActiveDuration (24h) + GracePeriod (24h) = 48h.
        // Advance past the full 48h window so `first` falls out of the pool.
        _time.Advance(TimeSpan.FromHours(50));

        await _sut.RotateAsync();
        var validation = await _sut.GetValidationKeysAsync();

        validation.Should().HaveCount(1);
        validation.Single().KeyId.Should().NotBe(first.KeyId);
    }

    [Fact]
    public async Task GetActiveSigningKeyAsync_ShouldBootstrapKey_WhenStoreEmpty()
    {
        var key = await _sut.GetActiveSigningKeyAsync();

        key.Should().NotBeNull();
        key.IsActive.Should().BeTrue();

        // Bootstrapped key must persist for subsequent reads.
        var again = await _sut.GetActiveSigningKeyAsync();
        again.KeyId.Should().Be(key.KeyId);
    }

    [Fact]
    public async Task RotateAsync_ShouldEmitDistinctKeyMaterial_OnEachCall()
    {
        var k1 = await _sut.RotateAsync();
        var k2 = await _sut.RotateAsync();

        k1.Key.Should().NotBe(k2.Key);
        k1.KeyId.Should().NotBe(k2.KeyId);
    }

    /// <summary>
    /// Tiny <see cref="TimeProvider"/> stand-in so this test file does not need
    /// the <c>Microsoft.Extensions.TimeProvider.Testing</c> NuGet (not in the
    /// Platform package matrix). Mirrors the spelling of FakeTimeProvider so a
    /// future drop-in of the official package would be a one-line swap.
    /// </summary>
    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public TestTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
