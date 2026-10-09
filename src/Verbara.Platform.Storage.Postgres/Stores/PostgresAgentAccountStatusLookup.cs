using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres <see cref="IAgentAccountStatusLookup"/> (licensed-agent-metering, design D2): one indexed
/// read of <c>users</c> per decision, no cache, so a status write takes effect on the next routing or
/// provisioning decision.
/// </summary>
public sealed class PostgresAgentAccountStatusLookup : IAgentAccountStatusLookup
{
    internal const string ActiveUserIdsSql =
        "SELECT user_id FROM users WHERE tenant_id = @TenantId AND user_id = ANY(@Ids) AND status = @Active";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresAgentAccountStatusLookup(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<IReadOnlySet<EntityId>> GetActiveUserIdsAsync(
        TenantId tenantId, IReadOnlyCollection<EntityId> userIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
            return new HashSet<EntityId>();

        var rows = await _dataSource.QueryListAsync(
            ActiveUserIdsSql,
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Ids", userIds.Select(id => id.Value).Distinct().ToArray()));
                p.Add(new NpgsqlParameter("Active", NpgsqlDbType.Integer) { Value = (int)UserStatus.Active });
            },
            static r => r.GetString(r.GetOrdinal("user_id")),
            ct).ConfigureAwait(false);

        return rows.Select(EntityId.From).ToHashSet();
    }
}
