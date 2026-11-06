using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Storage.Postgres;
using Verbara.Sdk.Cluster.Postgres.Migrations;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The Api's startup precondition for clustered voice (call-records-are-written-once design D13a): the event store's
/// fence locks the voice leader lease row inside each append's own transaction (D13), so the event store's database
/// must be the one holding the Sdk's <c>cluster_distributed_lock</c>. Run at registration, immediately before the dialer
/// registers, because <c>UsePostgresDialerStorage</c> and <c>UsePostgresEventStore</c> run their schema migrators at
/// registration on the event store's chain; the probe must speak first so a wrong database, a standby, an unreachable
/// host or a role without the fence's rights is refused with its diagnostic, never with a migrator's bare error.
/// </summary>
/// <remarks>
/// <para>Scope: only when clustered voice is on (<c>Asterisk:Ami:Hostname</c> set, so the voice lease is registered), a
/// lease database is configured (<c>ConnectionStrings:Cluster ?? Postgres</c>) and an event store is
/// (<c>ConnectionStrings:Analytics ?? Dialer ?? Postgres</c>). When the two resolved strings are ordinally equal the probe
/// issues no query: they are one data source. Strings, never data source identity: <c>ResolveDataSource</c> builds a new
/// data source per non-core string.</para>
/// <para>Otherwise a fail-closed round trip: (1) insert a random per-instance row through the lease database; (2) through
/// the event store's database, in one transaction, lock it with the fence's own <c>SELECT … FOR NO KEY UPDATE</c> and
/// write a token back, then commit; (3) read the token through the lease database; (4) delete the row. Any discrepancy or
/// error refuses to start, naming both databases and hosts (never a password), the key that supplied each and the failed
/// step. Never by parsed host/port/database: the same database behind a transaction-mode pooler and a direct host is
/// legitimate. A crashed probe's leftover row of this instance, once expired, is deleted by the next probe.</para>
/// </remarks>
internal static class EventStoreLeaseColocation
{
    /// <summary>The resource prefix of every probe row: <c>verbara:db-identity-probe:&lt;instance&gt;:&lt;random&gt;</c>.</summary>
    internal const string ProbeResourcePrefix = "verbara:db-identity-probe:";

    /// <summary>The diagnostics names of the probe's two short-lived data sources.</summary>
    internal const string LeaseDataSourceName = "colocation-probe-lease";

    /// <inheritdoc cref="LeaseDataSourceName"/>
    internal const string EventStoreDataSourceName = "colocation-probe-eventstore";

    private const int StatementTimeoutSeconds = 10;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeRowLifetime = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Runs the probe when <paramref name="configuration"/> puts clustered voice on two connection strings; returns at
    /// once otherwise. Throws <see cref="EventStoreLeaseColocationException"/> when the two do not reach one writable
    /// database with the fence's rights.
    /// </summary>
    public static async Task EnsureAsync(IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (Plan(configuration) is not { } plan)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        await using var lease = NamedDataSource.Build(Bounded(plan.Lease.ConnectionString), LeaseDataSourceName);
        await using var events = NamedDataSource.Build(Bounded(plan.Events.ConnectionString), EventStoreDataSourceName);
        await new Probe(plan, lease, events).RunAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>The two resolved chains when the probe must run; <see langword="null"/> when it must not.</summary>
    internal static ProbePlan? Plan(IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["Asterisk:Ami:Hostname"]))
            return null; // no AMI: no voice leader lease, nothing to fence

        // The same resolution as Program.cs: lease `Cluster ?? Postgres`; event store `Analytics ?? (Dialer ?? Postgres)`.
        var postgres = configuration.GetConnectionString("Postgres");
        var cluster = configuration.GetConnectionString("Cluster");
        var lease = ConnectionStringDefaults.ApplyPoolDefaults(cluster ?? postgres);
        if (string.IsNullOrEmpty(lease))
            return null; // no cluster connection: no lease

        var analytics = configuration.GetConnectionString("Analytics");
        var dialer = configuration.GetConnectionString("Dialer");
        var events = ConnectionStringDefaults.ApplyPoolDefaults(analytics)
            ?? ConnectionStringDefaults.ApplyPoolDefaults(dialer ?? postgres) ?? "";
        if (string.IsNullOrEmpty(events))
            return null; // no event store
        if (string.Equals(lease, events, StringComparison.Ordinal))
            return null; // one data source: nothing to prove

        var leaseKey = cluster is not null ? "ConnectionStrings:Cluster" : "ConnectionStrings:Postgres";
        var eventsKey = analytics is not null
            ? "ConnectionStrings:Analytics"
            : dialer is not null ? "ConnectionStrings:Dialer (fallback for Analytics)" : "ConnectionStrings:Postgres";
        var instance = configuration["Cluster:Leadership:InstanceId"] is { Length: > 0 } configured
            ? configured
            : Environment.MachineName;
        return new ProbePlan(new ProbeChain(lease, leaseKey), new ProbeChain(events, eventsKey), instance);
    }

    private static string Bounded(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = StatementTimeoutSeconds,
            CommandTimeout = StatementTimeoutSeconds,
        }.ConnectionString;

    /// <summary>One resolved connection string and the configuration key that supplied it.</summary>
    internal sealed record ProbeChain(string ConnectionString, string Key);

    /// <summary>The lease chain, the event store's chain and this instance's probe-row prefix.</summary>
    internal sealed record ProbePlan(ProbeChain Lease, ProbeChain Events, string Instance);

    private sealed class Probe(ProbePlan plan, NpgsqlDataSource lease, NpgsqlDataSource events)
    {
        private readonly string _resource =
            $"{ProbeResourcePrefix}{plan.Instance}:{Guid.NewGuid():N}";

        private readonly string _token = Guid.NewGuid().ToString("N");

        private string _step = "ensure the lock schema (lease database)";
        private bool _inserted;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await MigrationRunner.EnsureSchemaAsync(lease, NullLogger.Instance, cancellationToken)
                    .ConfigureAwait(false);

                _step = "1 delete this instance's expired probe rows (lease database)";
                await ExecuteAsync(lease, """
                    DELETE FROM cluster_distributed_lock
                     WHERE starts_with(resource, @prefix) AND expires_at < statement_timestamp()
                    """, p => p.Add(Text("prefix", $"{ProbeResourcePrefix}{plan.Instance}:")), cancellationToken)
                    .ConfigureAwait(false);

                _step = "1 insert the probe row (lease database)";
                await ExecuteAsync(lease, """
                    INSERT INTO cluster_distributed_lock (resource, owner, expires_at)
                    VALUES (@resource, @owner, statement_timestamp() + @lifetime)
                    """, p =>
                    {
                        p.Add(Text("resource", _resource));
                        p.Add(Text("owner", plan.Instance));
                        p.Add(new NpgsqlParameter("lifetime", NpgsqlDbType.Interval) { Value = ProbeRowLifetime });
                    }, cancellationToken).ConfigureAwait(false);
                _inserted = true;

                await LockAndWriteBackAsync(cancellationToken).ConfigureAwait(false);

                _step = "3 read the token back (lease database)";
                await using (var read = lease.CreateCommand(
                    "SELECT owner FROM cluster_distributed_lock WHERE resource = @resource"))
                {
                    read.Parameters.Add(Text("resource", _resource));
                    var owner = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                    if (!string.Equals(owner, _token, StringComparison.Ordinal))
                    {
                        throw await RefuseAsync(
                            "the token written through the event store's database is not what the lease database reads, " +
                            "so the two connection strings reach different databases",
                            sqlState: null, inner: null).ConfigureAwait(false);
                    }
                }

                _step = "4 delete the probe row (lease database)";
                await DeleteProbeRowAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (EventStoreLeaseColocationException)
            {
                await DeleteProbeRowBestEffortAsync().ConfigureAwait(false);
                throw;
            }
            catch (PostgresException ex)
            {
                await DeleteProbeRowBestEffortAsync().ConfigureAwait(false);
                throw await RefuseAsync($"PostgreSQL error {ex.SqlState}: {ex.MessageText}", ex.SqlState, ex)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException
                                           or System.Net.Sockets.SocketException)
            {
                await DeleteProbeRowBestEffortAsync().ConfigureAwait(false);
                throw await RefuseAsync($"unreachable or timed out ({ex.GetType().Name}: {ex.Message})", null, ex)
                    .ConfigureAwait(false);
            }
        }

        private async Task LockAndWriteBackAsync(CancellationToken cancellationToken)
        {
            _step = "2 open a transaction (event store database)";
            await using var connection = await events.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            _step = "2 lock the probe row with SELECT ... FOR NO KEY UPDATE (event store database)";
            await using (var select = new NpgsqlCommand("""
                SELECT owner FROM cluster_distributed_lock WHERE resource = @resource FOR NO KEY UPDATE
                """, connection, transaction))
            {
                select.Parameters.Add(Text("resource", _resource));
                if (await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    throw await RefuseAsync(
                        "the event store's database has no probe row (0 rows): it is another database or another " +
                        "cluster than the lease's", sqlState: null, inner: null).ConfigureAwait(false);
                }
            }

            _step = "2 write the token back (event store database)";
            await using (var update = new NpgsqlCommand(
                "UPDATE cluster_distributed_lock SET owner = @token WHERE resource = @resource", connection, transaction))
            {
                update.Parameters.Add(Text("token", _token));
                update.Parameters.Add(Text("resource", _resource));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _step = "2 commit (event store database)";
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task DeleteProbeRowAsync(CancellationToken cancellationToken)
        {
            await ExecuteAsync(lease, "DELETE FROM cluster_distributed_lock WHERE resource = @resource",
                p => p.Add(Text("resource", _resource)), cancellationToken).ConfigureAwait(false);
            _inserted = false;
        }

        private async Task DeleteProbeRowBestEffortAsync()
        {
            if (!_inserted)
                return;
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(StatementTimeoutSeconds));
                await DeleteProbeRowAsync(cleanup.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // best effort: the row expires and the next probe of this instance deletes it
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        private async Task<EventStoreLeaseColocationException> RefuseAsync(
            string reason, string? sqlState, Exception? inner)
        {
            var message = new StringBuilder()
                .Append("Clustered voice needs the event store and the voice leader lease in one writable database ")
                .Append("(call-records-are-written-once D13a): the event store's fence locks the lease row inside each ")
                .Append("append's own transaction. The startup probe failed at step '").Append(_step).Append("': ")
                .Append(reason).Append(". Lease database: ").Append(Describe(plan.Lease))
                .Append(". Event store database: ").Append(Describe(plan.Events)).Append('.');
            if (sqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                message.Append(" Grant the event store's role SELECT and UPDATE on cluster_distributed_lock (the fence ")
                    .Append("takes a FOR NO KEY UPDATE row lock), or use the lease's role for both connection strings.");
            }
            else if (sqlState == PostgresErrorCodes.ReadOnlySqlTransaction)
            {
                message.Append(" The event store's database is read-only (a standby or a read replica): point it at ")
                    .Append("the primary that holds the lease.");
            }
            else if (sqlState == PostgresErrorCodes.UndefinedTable)
            {
                message.Append(" The event store's database has no cluster_distributed_lock: it is not the lease's database.");
            }

            message.Append(" Set ConnectionStrings:Cluster and the event store's key to the same database.");
            var decoration = await DecorateAsync().ConfigureAwait(false);
            if (decoration.Length > 0)
                message.Append(" Server facts (informational): ").Append(decoration).Append('.');
            return new EventStoreLeaseColocationException(message.ToString(), _step, sqlState, inner);
        }

        private async Task<string> DecorateAsync()
        {
            var leaseFacts = await DecorateOneAsync(lease, "lease").ConfigureAwait(false);
            var eventFacts = await DecorateOneAsync(events, "event store").ConfigureAwait(false);
            return string.Join("; ", new[] { leaseFacts, eventFacts }.Where(s => s.Length > 0));
        }

        private static async Task<string> DecorateOneAsync(NpgsqlDataSource dataSource, string side)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await using var command = dataSource.CreateCommand("""
                    SELECT current_database(), (pg_control_system()).system_identifier, pg_is_in_recovery(),
                           host(inet_server_addr()), pg_postmaster_start_time()
                    """);
                await using var reader = await command.ExecuteReaderAsync(cts.Token).ConfigureAwait(false);
                if (!await reader.ReadAsync(cts.Token).ConfigureAwait(false))
                    return "";
                return string.Create(CultureInfo.InvariantCulture,
                    $"{side} database '{reader.GetString(0)}' system_identifier {reader.GetInt64(1)}, " +
                    $"in recovery {reader.GetBoolean(2)}, server address " +
                    $"{(reader.IsDBNull(3) ? "(local socket)" : reader.GetString(3))}, " +
                    $"started {reader.GetFieldValue<DateTimeOffset>(4):O}");
            }
#pragma warning disable CA1031 // decoration only: it never decides and never hides the refusal
            catch (Exception)
#pragma warning restore CA1031
            {
                return "";
            }
        }

        private static string Describe(ProbeChain chain)
        {
            var builder = new NpgsqlConnectionStringBuilder(chain.ConnectionString);
            return string.Create(CultureInfo.InvariantCulture,
                $"'{builder.Database}' on {builder.Host}:{builder.Port} from {chain.Key}");
        }

        private static NpgsqlParameter Text(string name, string value) =>
            new(name, NpgsqlDbType.Text) { Value = value };

        private static async Task ExecuteAsync(
            NpgsqlDataSource dataSource, string sql, Action<NpgsqlParameterCollection> bind,
            CancellationToken cancellationToken)
        {
            await using var command = dataSource.CreateCommand(sql);
            bind(command.Parameters);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The Api refused to start: clustered voice is configured on a lease database and an event store database that the
/// startup round trip could not prove are one writable database with the fence's rights (design D13a).
/// </summary>
public sealed class EventStoreLeaseColocationException : InvalidOperationException
{
    /// <summary>Creates the refusal.</summary>
    public EventStoreLeaseColocationException(string message, string step, string? sqlState, Exception? innerException)
        : base(message, innerException)
    {
        Step = step;
        SqlState = sqlState;
    }

    /// <summary>The probe step that failed.</summary>
    public string Step { get; }

    /// <summary>The PostgreSQL error code that refused, when one did (e.g. <c>42P01</c>, <c>25006</c>, <c>42501</c>).</summary>
    public string? SqlState { get; }
}
