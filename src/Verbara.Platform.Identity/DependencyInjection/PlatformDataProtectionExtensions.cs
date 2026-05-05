using System.Diagnostics.CodeAnalysis;
using Verbara.Platform.Identity.DataProtection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Identity.DependencyInjection;

/// <summary>
/// DI extension that wires ASP.NET Core DataProtection with a safe-by-default
/// persistence strategy. See ADR-0003 (DataProtection key persistence strategy).
/// </summary>
/// <remarks>
/// Default behavior is DB-backed via <see cref="PlatformDataProtectionDbContext"/>;
/// the consumer must register the DbContext before calling
/// <see cref="AddPlatformDataProtection"/>. Use
/// <see cref="PlatformDataProtectionOptions.UseFileSystem"/> for single-node deploys
/// or <see cref="PlatformDataProtectionOptions.UseEphemeralKeysForTesting"/> for
/// unit-test scenarios.
/// </remarks>
public static partial class PlatformDataProtectionExtensions
{
    /// <summary>
    /// Logger category used to emit the warning when ephemeral keys are selected.
    /// </summary>
    private const string EphemeralWarningCategory = "Verbara.Platform.Identity.DataProtection";

    /// <summary>
    /// Registers the DataProtection stack with persistence as configured by
    /// <paramref name="configure"/>. Default is DB-backed via
    /// <see cref="PlatformDataProtectionDbContext"/> per ADR-0003.
    /// </summary>
    [RequiresUnreferencedCode("EF Core path uses reflection; not trim-safe.")]
    [RequiresDynamicCode("EF Core path builds queries dynamically; not NativeAOT-safe.")]
    public static IServiceCollection AddPlatformDataProtection(
        this IServiceCollection services,
        Action<PlatformDataProtectionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PlatformDataProtectionOptions();
        configure(options);

        var builder = services.AddDataProtection()
            .SetApplicationName(options.ApplicationName);

        if (options.UseEphemeralForTesting)
        {
            // No PersistKeys* call → ASP.NET Core falls back to its in-memory
            // key store. We surface the warning so misconfigured production
            // deployments are noisy in logs.
            services.AddSingleton<EphemeralKeyringWarning>();
        }
        else if (!string.IsNullOrEmpty(options.FileSystemPath))
        {
            builder.PersistKeysToFileSystem(new DirectoryInfo(options.FileSystemPath));
        }
        else
        {
            // DEFAULT (per ADR-0003): DB-backed via PlatformDataProtectionDbContext.
            // The DbContext MUST be registered separately by the consumer (e.g. via
            // services.AddDbContext<PlatformDataProtectionDbContext>(...)). We do
            // NOT register the DbContext here because the consumer owns the
            // connection-string + provider choice (Postgres in production,
            // InMemory in tests).
            builder.PersistKeysToDbContext<PlatformDataProtectionDbContext>();
        }

        return services;
    }

    /// <summary>
    /// Tiny hosted-style helper that logs a single warning at construction time
    /// when ephemeral keys are selected. Resolved by the test that exercises the
    /// ephemeral path; production code never opts in.
    /// </summary>
    internal sealed partial class EphemeralKeyringWarning
    {
        public EphemeralKeyringWarning(ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            var logger = loggerFactory.CreateLogger(EphemeralWarningCategory);
            LogEphemeralWarning(logger);
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
            Message = "DataProtection is using EPHEMERAL in-memory keys. Encrypted-at-rest credentials WILL be lost on process restart. Use only for tests/CI.")]
        private static partial void LogEphemeralWarning(ILogger logger);
    }
}

/// <summary>
/// Options surface for <see cref="PlatformDataProtectionExtensions.AddPlatformDataProtection"/>.
/// Default values produce DB-backed persistence via
/// <see cref="PlatformDataProtectionDbContext"/>.
/// </summary>
public sealed class PlatformDataProtectionOptions
{
    /// <summary>
    /// DataProtection application name. Defaults to <c>"Verbara.Platform"</c>.
    /// Different consumers (e.g. Platform.Api vs Platform.Mail) use different
    /// names so their key isolation does not collide.
    /// </summary>
    public string ApplicationName { get; set; } = "Verbara.Platform";

    /// <summary>
    /// True when the consumer asked for ephemeral (in-memory) keys via
    /// <see cref="UseEphemeralKeysForTesting"/>. Test/CI only.
    /// </summary>
    public bool UseEphemeralForTesting { get; private set; }

    /// <summary>
    /// File-system path the keyring should be persisted to, when set via
    /// <see cref="UseFileSystem"/>. Single-node deploy convenience.
    /// </summary>
    public string? FileSystemPath { get; private set; }

    /// <summary>
    /// Persist keys to the given directory instead of the DB. Suitable for
    /// single-node deploys with a stable mounted volume. Mutually exclusive
    /// with <see cref="UseEphemeralKeysForTesting"/>.
    /// </summary>
    public PlatformDataProtectionOptions UseFileSystem(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must be non-empty.", nameof(path));

        FileSystemPath = path;
        return this;
    }

    /// <summary>
    /// Skip persistence — ASP.NET Core's default in-memory keyring is used.
    /// Test/CI only — production callers must use the default DB-backed path
    /// or <see cref="UseFileSystem"/>.
    /// </summary>
    public PlatformDataProtectionOptions UseEphemeralKeysForTesting()
    {
        UseEphemeralForTesting = true;
        return this;
    }
}
