using Npgsql;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// ADR-0015 Phase 1 mitigation: applies sane SMB-tier <c>Maximum Pool Size</c>,
/// <c>Minimum Pool Size</c>, and <c>Connection Idle Lifetime</c> defaults to
/// PostgreSQL connection strings if (and only if) the operator did not specify
/// them.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="NpgsqlDataSource"/> across Platform.Storage.Postgres + the
/// Pro storage packages parses the same connection-string keywords. Without
/// explicit operator override, Npgsql defaults each pool to
/// <c>Maximum Pool Size=100</c>; with ~14 known data sources sharing a single
/// connection string in a typical SMB deployment, theoretical worst-case
/// demand from one platform-api instance is 1 400 connections — enough to
/// blow past <c>max_connections=100</c> (postgres-alpine default) under any
/// concurrent burst.
/// </para>
/// <para>
/// Phase 1 (this helper) caps demand at 14 × 10 = 140 connections per
/// instance, comfortable under the SMB tier <c>max_connections=200</c>
/// shipped in <c>docker-compose.smb.yml</c> and
/// <c>docker-compose.production.yml</c>. Phase 2 (Pro 1.16.0-pro shared
/// <see cref="NpgsqlDataSource"/> overload) eliminates the sprawl entirely
/// — when that ships, Phase 1 stays in place as a safety net for operators
/// who haven't tuned their connection string explicitly.
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
    /// Default per-data-source <c>Maximum Pool Size</c>. 14 known data sources
    /// × 10 = 140 connection demand ceiling per platform-api instance.
    /// </summary>
    public const int DefaultMaximumPoolSize = 10;

    /// <summary>
    /// Default per-data-source <c>Minimum Pool Size</c>. Keeps a small warm
    /// pool to avoid cold-start latency on the auth hot path; small enough
    /// that 14 idle data sources only pin 28 connections total.
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
