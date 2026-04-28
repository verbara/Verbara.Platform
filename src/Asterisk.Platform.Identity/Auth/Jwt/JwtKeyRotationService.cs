using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Identity.Auth.Jwt;

/// <summary>
/// Default <see cref="IJwtKeyRotationService"/> implementation. Persists keys
/// through <see cref="IJwtKeyStore"/> + emits per-rotation metrics under the
/// <c>Asterisk.Platform.Auth.JwtKeyRotation</c> meter.
/// </summary>
/// <remarks>
/// R5.4 S5.9 — Closes C.1 of post-R5.1 triage. AOT-safe: no reflection, all
/// crypto material via <see cref="RandomNumberGenerator"/>, all latency tracked
/// via <see cref="Histogram{T}"/>.
/// </remarks>
public sealed class JwtKeyRotationService : IJwtKeyRotationService, IDisposable
{
    /// <summary>The meter name for JWT rotation observability.</summary>
    public const string MeterName = "Asterisk.Platform.Auth.JwtKeyRotation";

    private readonly IJwtKeyStore _store;
    private readonly TimeProvider _time;
    private readonly JwtKeyRotationOptions _options;
    private readonly Meter _meter;
    private readonly Histogram<double> _rotationDuration;
    private readonly ObservableGauge<int> _activeKeysGauge;
    private volatile int _validationKeyCountSnapshot;

    /// <summary>Creates a new rotation service from the supplied store + clock + options.</summary>
    public JwtKeyRotationService(
        IJwtKeyStore store,
        TimeProvider time,
        IOptions<JwtKeyRotationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _time = time;
        _options = options.Value;
        _meter = new Meter(MeterName);
        _rotationDuration = _meter.CreateHistogram<double>(
            "jwt.key.rotation.duration",
            unit: "ms",
            description: "Latency of JWT signing-key rotation operations.");
        _activeKeysGauge = _meter.CreateObservableGauge(
            "jwt.keys.active",
            () => _validationKeyCountSnapshot,
            unit: "{keys}",
            description: "Number of validation keys currently in the rotation pool.");
    }

    /// <inheritdoc />
    public async Task<JwtKeyEntry> RotateAsync(CancellationToken ct = default)
    {
        var startTimestamp = _time.GetTimestamp();
        var now = _time.GetUtcNow();
        var bytes = RandomNumberGenerator.GetBytes(_options.KeySizeBytes);
        var entry = new JwtKeyEntry
        {
            KeyId = Guid.NewGuid().ToString("N"),
            Key = Convert.ToBase64String(bytes),
            // AHH Phase 3 — explicit Hs256 for new rotations. Pre-Phase-3
            // R5.4 entries didn't carry this field and deserialize with the
            // same Hs256 default per JwtKeyEntry initializer.
            Algorithm = JwtKeyAlgorithm.Hs256,
            ActivatedAt = now,
            ExpiresAt = now.Add(_options.ActiveDuration + _options.GracePeriod),
            IsActive = true,
        };
        await _store.UpsertAsync(entry, ct).ConfigureAwait(false);
        await _store.RemoveExpiredAsync(now, ct).ConfigureAwait(false);

        var validation = await _store.GetAllAsync(ct).ConfigureAwait(false);
        _validationKeyCountSnapshot = validation.Count(e => e.ExpiresAt > now);

        var elapsed = _time.GetElapsedTime(startTimestamp).TotalMilliseconds;
        _rotationDuration.Record(elapsed);

        return entry;
    }

    /// <inheritdoc />
    public async Task<JwtKeyEntry> GetActiveSigningKeyAsync(CancellationToken ct = default)
    {
        var active = await _store.GetActiveAsync(ct).ConfigureAwait(false);
        return active ?? await RotateAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JwtKeyEntry>> GetValidationKeysAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var all = await _store.GetAllAsync(ct).ConfigureAwait(false);
        var live = all.Where(e => e.ExpiresAt > now).ToArray();
        _validationKeyCountSnapshot = live.Length;
        return live;
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
