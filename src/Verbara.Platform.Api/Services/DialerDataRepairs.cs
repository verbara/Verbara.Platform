using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The one-shot, audited repairs of campaigns the console stored wrongly (dialer-honours-dnc-callbacks-and-agent-
/// dispositions, design D1 and D13), run against the dialer's database once per database:
/// <list type="bullet">
/// <item><see cref="DncRepairStep"/> (#472): every campaign created before v3.25.1-web (the first console that sent
/// <c>dncEnabled</c>) and stored with its DNC check off gets it on, audited <c>campaign.dnc_repaired</c>.</item>
/// <item><see cref="AbandonRateRepairStep"/> (Web #461, owner Q5): every predictive campaign whose
/// <c>target_abandon_rate</c> is above 1 (the console's target wait in seconds) gets <c>0.03</c>, audited
/// <c>campaign.abandon_rate_repaired</c> with the old and the new value.</item>
/// </list>
/// Each step is a row in Platform's own ledger, <c>platform_one_shot_repairs</c>, created here on first use in the
/// dialer's database: the step runs only while its row is missing and writes it in the same transaction as its
/// update, under a transaction-scoped Postgres lock, so it runs once whatever the number of replicas, and an
/// administrator's later change is never undone. A database without the dialer schema is left unchanged (no ledger
/// row either, so the step runs once the schema exists). The audit entries are written after the commit.
/// </summary>
internal sealed partial class DialerDataRepairs
{
    /// <summary>The ledger step name of the DNC repair.</summary>
    internal const string DncRepairStep = "campaign.dnc_repaired";

    /// <summary>The ledger step name of the abandon-rate repair.</summary>
    internal const string AbandonRateRepairStep = "campaign.abandon_rate_repaired";

    /// <summary>The v3.25.1-web tag (2026-10-23 10:08 UTC): campaigns created before it may never have sent <c>dncEnabled</c>.</summary>
    internal static readonly DateTimeOffset DncCutoff = new(2026, 10, 23, 10, 8, 0, TimeSpan.Zero);

    /// <summary>The console's and the API's default rate.</summary>
    internal const double DefaultAbandonRate = 0.03;

    /// <summary>Pro's <c>DialingMode.Predictive</c>.</summary>
    private const int PredictiveMode = 4;

    /// <summary>Serializes the repairs across replicas (an arbitrary constant key: "VRBRPAIR").</summary>
    private const long RepairLockKey = 0x5652425250414952;

    private const string ActorId = "verbara:system:dialer-data-repair";

    private readonly NpgsqlDataSource _dialerDataSource;
    private readonly IAuditService _audit;
    private readonly ILogger<DialerDataRepairs> _logger;

    public DialerDataRepairs(NpgsqlDataSource dialerDataSource, IAuditService audit, ILogger<DialerDataRepairs> logger)
    {
        _dialerDataSource = dialerDataSource;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Runs both steps, each at most once per database.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await RunStepAsync(
            DncRepairStep,
            "UPDATE campaigns SET check_global_dnc = TRUE " +
            "WHERE check_global_dnc = FALSE AND created_at < @Cutoff " +
            "RETURNING tenant_id, id, 0::double precision AS old_rate",
            p => p.Add(new NpgsqlParameter("Cutoff", NpgsqlDbType.TimestampTz) { Value = DncCutoff.UtcDateTime }),
            (tenantId, campaignId, _) => _audit.RecordAsync(
                new TenantId(tenantId), category: "config", action: DncRepairStep, severity: "warning",
                actorId: ActorId, actorType: "system",
                targetId: campaignId.ToString(CultureInfo.InvariantCulture), targetType: "campaign",
                metadata: new Dictionary<string, string>
                {
                    ["dncEnabled"] = "true",
                    ["reason"] = "created before 2026-10-23T10:08:00Z with the DNC check off (#472)",
                },
                ct: ct),
            ct).ConfigureAwait(false);

        await RunStepAsync(
            AbandonRateRepairStep,
            "UPDATE campaigns c SET target_abandon_rate = @Rate " +
            "FROM (SELECT id, target_abandon_rate FROM campaigns WHERE mode = @Predictive AND target_abandon_rate > 1 FOR UPDATE) o " +
            "WHERE c.id = o.id " +
            "RETURNING c.tenant_id, c.id, o.target_abandon_rate AS old_rate",
            p =>
            {
                p.Add(new NpgsqlParameter("Rate", NpgsqlDbType.Double) { Value = DefaultAbandonRate });
                p.Add(new NpgsqlParameter("Predictive", NpgsqlDbType.Integer) { Value = PredictiveMode });
            },
            (tenantId, campaignId, oldRate) => _audit.RecordAsync(
                new TenantId(tenantId), category: "config", action: AbandonRateRepairStep, severity: "warning",
                actorId: ActorId, actorType: "system",
                targetId: campaignId.ToString(CultureInfo.InvariantCulture), targetType: "campaign",
                metadata: new Dictionary<string, string>
                {
                    ["oldTargetAbandonRate"] = oldRate.ToString("R", CultureInfo.InvariantCulture),
                    ["newTargetAbandonRate"] = DefaultAbandonRate.ToString("R", CultureInfo.InvariantCulture),
                    ["reason"] = "a predictive campaign stored a target wait as its abandon rate (Web #461)",
                },
                ct: ct),
            ct).ConfigureAwait(false);
    }

    private async Task RunStepAsync(
        string step,
        string updateSql,
        Action<NpgsqlParameterCollection> bind,
        Func<string, long, double, Task> audit,
        CancellationToken ct)
    {
        var repaired = new List<(string TenantId, long CampaignId, double OldRate)>();

        await using (var connection = await _dialerDataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            await ExecuteAsync(connection, transaction, $"SELECT pg_advisory_xact_lock({RepairLockKey})", ct).ConfigureAwait(false);

            await using (var probe = new NpgsqlCommand("SELECT to_regclass('campaigns') IS NOT NULL", connection, transaction))
            {
                if (await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)
                {
                    LogNoDialerSchema(step);
                    return;
                }
            }

            await ExecuteAsync(connection, transaction,
                "CREATE TABLE IF NOT EXISTS platform_one_shot_repairs (" +
                "step TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), changed INTEGER NOT NULL)",
                ct).ConfigureAwait(false);

            await using (var done = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM platform_one_shot_repairs WHERE step = @Step)", connection, transaction))
            {
                done.Parameters.Add(new NpgsqlParameter("Step", NpgsqlDbType.Text) { Value = step });
                if (await done.ExecuteScalarAsync(ct).ConfigureAwait(false) is true)
                    return;
            }

            await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
            {
                bind(update.Parameters);
                await using var reader = await update.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    repaired.Add((reader.GetString(0), reader.GetInt64(1), reader.GetDouble(2)));
            }

            await using (var ledger = new NpgsqlCommand(
                "INSERT INTO platform_one_shot_repairs (step, changed) VALUES (@Step, @Changed)", connection, transaction))
            {
                ledger.Parameters.Add(new NpgsqlParameter("Step", NpgsqlDbType.Text) { Value = step });
                ledger.Parameters.Add(new NpgsqlParameter("Changed", NpgsqlDbType.Integer) { Value = repaired.Count });
                await ledger.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        foreach (var (tenantId, campaignId, oldRate) in repaired)
            await audit(tenantId, campaignId, oldRate).ConfigureAwait(false);

        LogStepApplied(step, repaired.Count);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[DIALER-REPAIR] {Step}: applied once, {Count} campaign(s) changed and audited.")]
    private partial void LogStepApplied(string step, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[DIALER-REPAIR] {Step}: no dialer schema in this database; nothing to repair.")]
    private partial void LogNoDialerSchema(string step);
}

/// <summary>
/// Runs <see cref="DialerDataRepairs"/> once at start, off the host's start path: it waits for the host to finish
/// starting, then resolves the repairs (and with them the dialer's data source and the audit service) on first use,
/// never at <c>Host.StartAsync</c>. A failure is logged and retried at the next start (no ledger row was written).
/// </summary>
internal sealed partial class DialerDataRepairService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DialerDataRepairService> _logger;

    public DialerDataRepairService(IServiceProvider services, ILogger<DialerDataRepairService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await _services.GetRequiredService<DialerDataRepairs>().RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown before the repairs ran; they run at the next start.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRepairFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "[DIALER-REPAIR] The one-shot campaign repairs failed; they run again at the next start.")]
    private partial void LogRepairFailed(Exception ex);
}
