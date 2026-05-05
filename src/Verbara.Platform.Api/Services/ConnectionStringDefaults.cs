using Npgsql;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// ADR-0015 pool-sizing safety net: applies sane SMB-tier
/// <c>Maximum Pool Size</c>, <c>Minimum Pool Size</c>, and
/// <c>Connection Idle Lifetime</c> defaults to PostgreSQL connection strings
/// if (and only if) the operator did not specify them.
/// </summary>
/// <remarks>
/// <para>
/// Phase 2 (Platform v1.14.6 + Pro 1.16.0-pro shared
/// <see cref="NpgsqlDataSource"/> overload) collapses the historical
/// 14-DataSource sprawl into ONE shared pool per platform-api instance —
/// real Postgres demand is now ~10 conns + ancillary, not 14 × 10 = 140.
/// This helper still runs because it preserves the Phase 1 safety net for
/// the per-pool case: operators running unpatched stacks, custom Pro
/// builds, or Platform.Storage.Postgres without the shared overload still
/// get sane defaults instead of Npgsql's library default
/// <c>Maximum Pool Size=100</c>, which would blow past the SMB tier
/// <c>max_connections</c> ceiling under concurrent burst.
/// </para>
/// <para>
/// In the post-Phase-2 default path (the SMB tier compose stacks ship with
/// the shared pool), the values applied here cap the single shared pool at
/// 10 conns — comfortable under the SMB tier <c>max_connections=200</c>
/// shipped in <c>docker-compose.smb.yml</c> and
/// <c>docker-compose.production.yml</c>. The minimum required
/// <c>max_connections</c> per tier dropped 60–75% post-Phase-2 (see
/// <c>docs/operations/capacity-planning.md</c> § "Small / Medium / Large
/// / XL"); compose stacks retain the higher value as operator headroom.
/// </para>
/// <para>
/// Operator override is preserved: any deployment that explicitly sets
/// <c>Maximum Pool Size=N</c> (or any of the other tunables) in the
/// connection string keeps that value verbatim — this helper never
/// overwrites operator intent.
/// </para>
/// </remarks>
internal static class ConnectionStringDefaults
{
    /// <summary>
    /// Default per-data-source <c>Maximum Pool Size</c>. Post-Phase-2 the
    /// shared pool caps at 10 conns per platform-api instance; pre-Phase-2
    /// (per-pool path) the same 10 keeps total demand at 14 × 10 = 140
    /// under the SMB tier <c>max_connections</c> ceiling.
    /// </summary>
    public const int DefaultMaximumPoolSize = 10;

    /// <summary>
    /// Default per-data-source <c>Minimum Pool Size</c>. Keeps a small warm
    /// pool to avoid cold-start latency on the auth hot path. Post-Phase-2
    /// the single shared pool pins ~2 conns idle; pre-Phase-2 (per-pool)
    /// 14 idle pools pin 28 conns total — still well under tier ceiling.
    /// </summary>
    public const int DefaultMinimumPoolSize = 2;

    /// <summary>
    /// Default <c>Connection Idle Lifetime</c> (seconds). Idle connections
    /// over this age get released so a brief workload spike doesn't pin
    /// pool capacity for the rest of the instance lifetime.
    /// </summary>
    public const int DefaultConnectionIdleLifetimeSeconds = 300;

    /// <summary>
    /// Returns <paramref name="connectionString"/> with pool-sizing defaults
    /// applied if missing. Operator-specified values are preserved verbatim.
    /// Returns the input unchanged when null, empty, or whitespace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Operator intent is detected by case-insensitive substring match
    /// over the raw <paramref name="connectionString"/>:
    /// <see cref="NpgsqlConnectionStringBuilder.ContainsKey"/> reports
    /// <c>true</c> for typed properties even when they hold their library
    /// default value, so it cannot distinguish "operator chose default 100"
    /// from "operator left key absent". The substring approach is
    /// deterministic — if the operator wrote any of the recognised aliases
    /// in the connection string, we treat their value as authoritative.
    /// </para>
    /// </remarks>
    public static string? ApplyPoolDefaults(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var hasMaxPool = ContainsAnyKey(connectionString, "Maximum Pool Size", "Max Pool Size", "MaxPoolSize");
        var hasMinPool = ContainsAnyKey(connectionString, "Minimum Pool Size", "Min Pool Size", "MinPoolSize");
        var hasIdleLifetime = ContainsAnyKey(connectionString, "Connection Idle Lifetime", "ConnectionIdleLifetime");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        if (!hasMaxPool)
        {
            builder.MaxPoolSize = DefaultMaximumPoolSize;
        }

        if (!hasMinPool)
        {
            builder.MinPoolSize = DefaultMinimumPoolSize;
        }

        if (!hasIdleLifetime)
        {
            builder.ConnectionIdleLifetime = DefaultConnectionIdleLifetimeSeconds;
        }

        return builder.ToString();
    }

    private static bool ContainsAnyKey(string connectionString, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (connectionString.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
