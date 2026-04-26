using Asterisk.Sdk.Pro.Storage.Common.Retention;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Endpoints.Retention;

/// <summary>
/// Process-wide mutable retention configuration that the admin surface
/// (PATCH /management/retention/config) can flip at runtime.
/// </summary>
/// <remarks>
/// <para>
/// The Pro <see cref="RetentionService"/> takes a snapshot of
/// <see cref="IOptions{RetentionOptions}"/> at construction time, so flipping
/// <see cref="DryRun"/> on this state object affects <em>manual</em> runs
/// triggered by <c>POST /management/retention/run-now</c> immediately, but
/// the scheduled background loop continues using the value it captured at
/// startup.
/// </para>
/// <para>
/// This is documented as a known limitation in the admin UI banner so
/// operators understand the cadence: flip DryRun off, run-now to verify,
/// commit the change in app config, restart for the daily loop to honor it.
/// </para>
/// </remarks>
public sealed class RetentionAdminState
{
    private readonly object _lock = new();
    private bool _dryRun;
    private readonly bool _initialDryRun;

    public RetentionAdminState(IOptions<RetentionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _initialDryRun = options.Value.DryRun;
        _dryRun = _initialDryRun;
    }

    public bool DryRun
    {
        get { lock (_lock) return _dryRun; }
    }

    /// <summary>The startup-captured DryRun value (informational; never mutated).</summary>
    public bool InitialDryRun => _initialDryRun;

    /// <summary>Returns the previous DryRun value for audit metadata.</summary>
    public bool SetDryRun(bool value)
    {
        lock (_lock)
        {
            var prev = _dryRun;
            _dryRun = value;
            return prev;
        }
    }
}
