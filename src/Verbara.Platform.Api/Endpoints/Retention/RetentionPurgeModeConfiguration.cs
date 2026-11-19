namespace Verbara.Platform.Api.Endpoints.Retention;

/// <summary>
/// Reads the mode of the scheduled Pro retention purge from deployment configuration
/// (<c>Retention:DryRun</c>, environment variable <c>Retention__DryRun</c>).
/// </summary>
/// <remarks>
/// Absent or blank means dry run, the safe default. Any other value must parse as a boolean
/// (<see cref="bool.TryParse(string?, out bool)"/>, case-insensitive); anything else stops the host at startup
/// with an error naming the key, so a typo such as <c>flase</c> never leaves the purge counting while the operator
/// believes it deletes. Read by hand over the indexer, without the configuration binder, so it stays AOT-safe.
/// The key governs only the Pro purge; Platform's per-tenant conversation retention does not read it.
/// </remarks>
public static class RetentionPurgeModeConfiguration
{
    /// <summary>The configuration key that sets the scheduled Pro retention purge mode.</summary>
    public const string Key = "Retention:DryRun";

    /// <summary>
    /// Returns <see langword="true"/> when the scheduled purge must only count rows, <see langword="false"/> when
    /// it must delete them.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key holds a value that is not a boolean.</exception>
    public static bool Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[Key];
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        if (bool.TryParse(raw.Trim(), out var dryRun))
            return dryRun;

        throw new InvalidOperationException(
            $"Configuration key '{Key}' must be 'true' or 'false' (got '{raw}'). " +
            "It sets whether the scheduled Pro retention purge only counts expired rows (true, the default) " +
            "or deletes them (false).");
    }
}
