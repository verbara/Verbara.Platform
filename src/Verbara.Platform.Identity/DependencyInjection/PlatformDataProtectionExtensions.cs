using Verbara.Platform.Identity.DataProtection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Verbara.Platform.Identity.DependencyInjection;

/// <summary>
/// DI extension that wires ASP.NET Core DataProtection with a safe-by-default
/// persistence strategy. See ADR-0003 (DataProtection key persistence) and
/// ADR-0022 Phase B (EF Core → Dapper migration to unblock Native AOT on
/// Verbara.Platform.Api).
/// </summary>
/// <remarks>
/// Default behavior is Postgres-backed via <see cref="DapperXmlRepository"/>
/// against the <c>data_protection_keys</c> table (migrations V018 + V022).
/// Use <see cref="PlatformDataProtectionOptions.UseFileSystem"/> for single-node
/// deploys or <see cref="PlatformDataProtectionOptions.UseEphemeralKeysForTesting"/>
/// for unit-test scenarios.
/// </remarks>
public static partial class PlatformDataProtectionExtensions
{
    private const string EphemeralWarningCategory = "Verbara.Platform.Identity.DataProtection";

    /// <summary>
    /// Registers the DataProtection stack with persistence as configured by
    /// <paramref name="configure"/>. Default is Postgres-backed via
    /// <see cref="DapperXmlRepository"/>; the consumer MUST opt in by calling
    /// <see cref="PlatformDataProtectionOptions.UsePostgres"/> (or
    /// <see cref="PlatformDataProtectionOptions.UseFileSystem"/> /
    /// <see cref="PlatformDataProtectionOptions.UseEphemeralKeysForTesting"/>).
    /// </summary>
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
            services.AddSingleton<EphemeralKeyringWarning>();
        }
        else if (!string.IsNullOrEmpty(options.FileSystemPath))
        {
            builder.PersistKeysToFileSystem(new DirectoryInfo(options.FileSystemPath));
        }
        else if (options.PostgresDataSource is not null)
        {
            // ADR-0022 Phase B — Dapper-backed IXmlRepository replaces the
            // EF Core PersistKeysToDbContext path. The schema (V018 + V022) is
            // unchanged so existing keyrings transparently survive the cutover.
            var dataSource = options.PostgresDataSource;
            services.AddSingleton(sp => new DapperXmlRepository(
                dataSource,
                sp.GetRequiredService<ILogger<DapperXmlRepository>>()));
            services.AddSingleton<IXmlRepository>(sp => sp.GetRequiredService<DapperXmlRepository>());
            services.AddOptions<KeyManagementOptions>()
                .Configure<DapperXmlRepository>((opts, repo) => opts.XmlRepository = repo);
        }
        else
        {
            throw new InvalidOperationException(
                "PlatformDataProtectionOptions requires one of: UsePostgres(NpgsqlDataSource), " +
                "UseFileSystem(path), or UseEphemeralKeysForTesting(). Default no-op is unsafe.");
        }

        return services;
    }

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
/// Choose exactly one persistence strategy by calling
/// <see cref="UsePostgres"/>, <see cref="UseFileSystem"/>, or
/// <see cref="UseEphemeralKeysForTesting"/>.
/// </summary>
public sealed class PlatformDataProtectionOptions
{
    /// <summary>
    /// DataProtection application name. Defaults to <c>"Verbara.Platform"</c>.
    /// </summary>
    public string ApplicationName { get; set; } = "Verbara.Platform";

    /// <summary>True when the consumer asked for ephemeral keys via <see cref="UseEphemeralKeysForTesting"/>.</summary>
    public bool UseEphemeralForTesting { get; private set; }

    /// <summary>File-system path the keyring should be persisted to, when set via <see cref="UseFileSystem"/>.</summary>
    public string? FileSystemPath { get; private set; }

    /// <summary>NpgsqlDataSource for the Dapper-backed keyring, when set via <see cref="UsePostgres"/>.</summary>
    public NpgsqlDataSource? PostgresDataSource { get; private set; }

    /// <summary>
    /// Persist the DataProtection keyring in the shared Postgres
    /// <c>data_protection_keys</c> table via Dapper. Use the
    /// <see cref="NpgsqlDataSource"/> the host already owns (ADR-0015 Phase 2
    /// shared pool) so DataProtection does not open its own connection pool.
    /// </summary>
    public PlatformDataProtectionOptions UsePostgres(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        PostgresDataSource = dataSource;
        return this;
    }

    /// <summary>Persist keys to the given directory (single-node deploy convenience).</summary>
    public PlatformDataProtectionOptions UseFileSystem(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must be non-empty.", nameof(path));

        FileSystemPath = path;
        return this;
    }

    /// <summary>Skip persistence — ASP.NET Core's default in-memory keyring is used. Test/CI only.</summary>
    public PlatformDataProtectionOptions UseEphemeralKeysForTesting()
    {
        UseEphemeralForTesting = true;
        return this;
    }
}
