namespace Verbara.Platform.Api.Endpoints.Retention;

/// <summary>
/// Query + mutation surface for the retention admin view (R5.2 PC.1).
/// </summary>
public interface IRetentionAdminService
{
    /// <summary>
    /// Lists every registered <c>IRetentionTarget</c> with its current window
    /// + last-execution metadata (in-process tracker).
    /// </summary>
    IReadOnlyList<RetentionTargetDto> ListTargets();

    /// <summary>
    /// Returns the current global retention configuration snapshot.
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

    /// <summary>
    /// Sets the runtime <c>DryRun</c> flag. Returns the previous value so the
    /// caller can include it in the audit metadata.
    /// </summary>
    bool SetDryRun(bool value);
}
