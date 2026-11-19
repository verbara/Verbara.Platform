namespace Verbara.Platform.Api.Endpoints.Retention;

/// <summary>
/// Query + manual-run surface for the retention admin view (R5.2 PC.1). The scheduled purge mode is
/// read-only here (<see cref="IRetentionPurgeMode"/>).
/// </summary>
public interface IRetentionAdminService
{
    /// <summary>
    /// Lists every registered <c>IRetentionTarget</c> with its current window
    /// + last-execution metadata (in-process tracker).
    /// </summary>
    IReadOnlyList<RetentionTargetDto> ListTargets();

    /// <summary>
    /// Returns the current global retention configuration snapshot; its <c>DryRun</c> is the mode the
    /// scheduled purge of this process runs with.
    /// </summary>
    RetentionConfigDto GetConfig();

    /// <summary>
    /// Manually triggers a retention pass over <paramref name="targetName"/>
    /// (or all targets when <see langword="null"/>). Honors the
    /// <paramref name="dryRunOverride"/> flag immediately, regardless of the
    /// startup-captured <c>RetentionOptions.DryRun</c> value.
    /// </summary>
    Task<RetentionRunResultDto> RunNowAsync(
        bool dryRunOverride,
        string? targetName,
        CancellationToken ct);
}
