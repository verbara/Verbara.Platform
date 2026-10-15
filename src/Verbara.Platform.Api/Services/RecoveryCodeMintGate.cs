using System.Globalization;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The interval between two recovery-code mints for one user, read from <c>Mfa:RecoveryCodeMint:MinInterval</c>
/// (a <see cref="TimeSpan"/>; default 10 seconds; <c>00:00:00</c> turns the gate off).
/// </summary>
internal sealed record RecoveryCodeMintOptions(TimeSpan MinInterval)
{
    public static RecoveryCodeMintOptions Default { get; } = new(TimeSpan.FromSeconds(10));

    /// <summary>Read the key, falling back to <see cref="Default"/> for a missing, negative or unparsable value.</summary>
    public static RecoveryCodeMintOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var value = configuration["Mfa:RecoveryCodeMint:MinInterval"];
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed >= TimeSpan.Zero
            ? new RecoveryCodeMintOptions(parsed)
            : Default;
    }
}

/// <summary>
/// One recovery-code mint per user per <see cref="RecoveryCodeMintOptions.MinInterval"/>, shared by the four
/// mint endpoints (openspec <c>harden-recovery-code-digest</c>, design D10).
/// </summary>
/// <remarks>
/// <para>
/// A mint is ten Argon2id derivations at the password parameters: about 0.4 s of one core and about 1 GB
/// allocated (RC0), and the box stops scaling at about 8–9 of them per second. All four mint endpoints need
/// only the caller's own credentials (an access token, a TOTP the caller can compute for a secret it supplies,
/// or its own password), none is rate limited, and the lockout does not apply to them, so without this gate
/// about nine concurrent requests from one account could keep the whole box's Argon2id capacity busy and
/// queue every login behind them.
/// </para>
/// <para>
/// The allowance is spent when a mint starts, after the endpoint's own credential checks, so a refused
/// credential costs nothing and a concurrent second request is refused too. It limits only the caller's own
/// account: no user can use it to block another. It is per process (each replica keeps its own), which bounds
/// one account to one mint per interval per replica.
/// </para>
/// </remarks>
internal sealed class RecoveryCodeMintGate
{
    /// <summary>Above this many remembered users, a mint also forgets every entry whose interval has passed.</summary>
    private const int SweepThreshold = 1024;

    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly Dictionary<(TenantId, EntityId), DateTimeOffset> _lastMint = [];
    private readonly Lock _gate = new();

    public RecoveryCodeMintGate(RecoveryCodeMintOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _interval = options.MinInterval;
        _time = time;
    }

    /// <summary>
    /// Spend the user's mint allowance. Returns <see langword="false"/> with how long to wait when the user
    /// already minted within the interval; the caller must then mint nothing.
    /// </summary>
    public bool TryAcquire(TenantId tenantId, EntityId userId, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (_interval <= TimeSpan.Zero)
            return true;

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_lastMint.TryGetValue((tenantId, userId), out var last) && now - last < _interval)
            {
                retryAfter = _interval - (now - last);
                return false;
            }

            if (_lastMint.Count >= SweepThreshold)
            {
                foreach (var key in _lastMint.Where(e => now - e.Value >= _interval).Select(e => e.Key).ToList())
                    _lastMint.Remove(key);
            }

            _lastMint[(tenantId, userId)] = now;
            return true;
        }
    }

    /// <summary>
    /// The refusal: <c>429 Too Many Requests</c> with a <c>Retry-After</c> header in whole seconds (at least 1)
    /// and the shared <see cref="ErrorResponse"/> body.
    /// </summary>
    public static IResult Refuse(HttpContext context, TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(context);

        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return TypedResults.Json(
            new ErrorResponse("Recovery codes were generated moments ago. Wait before generating them again."),
            ApiJsonContext.Default.ErrorResponse,
            statusCode: StatusCodes.Status429TooManyRequests);
    }
}
