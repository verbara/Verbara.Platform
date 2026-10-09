using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Storage.Postgres.Licensing;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>A clock the test moves.</summary>
internal sealed class SettableClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>
/// licensed-agent-metering slice 2 — one fresh database (every migration, 019 included) with the ledger, the
/// writer, the daily close and the purge built over it, a settable clock and a settable licence id.
/// </summary>
internal sealed class LicenseLedgerHarness
{
    public const int Platform = 0;
    public const int Partner = 1;
    public const int Customer = 2;

    private LicenseLedgerHarness(NpgsqlDataSource ds, SettableClock clock, FixedLicenseIdSource licenses)
    {
        Ds = ds;
        Clock = clock;
        Licenses = licenses;
        Ledger = new LicenseAgentLedger(licenses, clock);
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Verbara.Platform.Storage.Postgres.Tests.LicenseLedger");
        Users = new PostgresUserStore(ds, services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());
        Writer = new PostgresLicensedAgentChangeWriter(ds, Users, Ledger);
        Close = new PostgresLicenseAgentDailyClose(ds, Ledger, NullLogger<PostgresLicenseAgentDailyClose>.Instance);
        Purge = new PostgresLicenseAgentRetentionPurge(ds);
    }

    public NpgsqlDataSource Ds { get; }
    public SettableClock Clock { get; }
    public FixedLicenseIdSource Licenses { get; }
    public LicenseAgentLedger Ledger { get; }
    public PostgresUserStore Users { get; }
    public PostgresLicensedAgentChangeWriter Writer { get; }
    public PostgresLicenseAgentDailyClose Close { get; }
    public PostgresLicenseAgentRetentionPurge Purge { get; }

    public static async Task<LicenseLedgerHarness> CreateAsync(
        AgentIdentityFixture fixture, DateTimeOffset now, string? licenseId = "lic-1")
    {
        var ds = await fixture.CreateDatabaseAsync(before018: false);
        return new LicenseLedgerHarness(ds, new SettableClock(now), new FixedLicenseIdSource(licenseId));
    }

    public Task TenantAsync(string tenantId, int type, string? parent = null, int status = 0) =>
        ExecAsync(
            "INSERT INTO tenants (tenant_id, name, status, type, parent_tenant_id, created_at, updated_at) " +
            "VALUES (@T, @T, @Status, @Type, @Parent, now(), now())",
            new NpgsqlParameter("T", tenantId),
            new NpgsqlParameter("Status", NpgsqlDbType.Integer) { Value = status },
            new NpgsqlParameter("Type", NpgsqlDbType.Integer) { Value = type },
            new NpgsqlParameter("Parent", NpgsqlDbType.Text) { Value = (object?)parent ?? DBNull.Value });

    public Task UserAsync(string tenantId, string userId, int status = 0) =>
        AgentIdentityFixture.InsertUserAsync(Ds, tenantId, userId, status);

    /// <summary>An agent row that already exists, written outside the ledger (the state an upgrade leaves).</summary>
    public Task ExistingAgentAsync(string tenantId, string agentId, string userId) =>
        AgentIdentityFixture.InsertAgentAsync(Ds, tenantId, agentId, userId);

    /// <summary>A Customer-style agent created through the writer, with its user.</summary>
    public async Task<Agent> CreateAgentAsync(string tenantId, string agentId, int userStatus = 0, string actor = "admin")
    {
        var userId = "u-" + agentId;
        await UserAsync(tenantId, userId, userStatus);
        var agent = NewAgent(tenantId, agentId, userId);
        await Writer.CommitAgentCreatedAsync(agent, actor, CancellationToken.None);
        return agent;
    }

    public static Agent NewAgent(string tenantId, string agentId, string userId) => new()
    {
        AgentId = EntityId.From(agentId),
        TenantId = new TenantId(tenantId),
        UserId = EntityId.From(userId),
        DisplayName = agentId,
        State = AgentState.Offline,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    public Task ConversationAsync(string tenantId, string conversationId, string ownerAgentId) =>
        ExecAsync(
            "INSERT INTO conversations (conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, metadata, created_at) " +
            "VALUES (@C, @T, 'contact-1', 0, 10, 2, @Owner, '{}'::jsonb, now())",
            new NpgsqlParameter("C", conversationId),
            new NpgsqlParameter("T", tenantId),
            new NpgsqlParameter("Owner", ownerAgentId));

    public async Task<IReadOnlyList<LicenseAgentEvent>> EventsAsync(string? tenantId)
    {
        var rows = new List<LicenseAgentEvent>();
        await using var cmd = Ds.CreateCommand(
            "SELECT * FROM license_agent_events WHERE chain_key = @K ORDER BY sequence");
        cmd.Parameters.Add(new NpgsqlParameter("K", LicenseAgentChain.ChainKeyOf(tenantId)));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(LicenseAgentRows.MapEvent(reader));
        return rows;
    }

    public async Task<IReadOnlyList<LicenseAgentDaily>> DailyAsync(string? tenantId)
    {
        var rows = new List<LicenseAgentDaily>();
        await using var cmd = Ds.CreateCommand(
            "SELECT * FROM license_agent_daily WHERE chain_key = @K ORDER BY sequence");
        cmd.Parameters.Add(new NpgsqlParameter("K", LicenseAgentChain.ChainKeyOf(tenantId)));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(LicenseAgentRows.MapDaily(reader));
        return rows;
    }

    public Task<long> CountAsync(string sql) => AgentIdentityFixture.ScalarAsync(Ds, sql);

    public async Task<string?> ScalarTextAsync(string sql)
    {
        await using var cmd = Ds.CreateCommand(sql);
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>Locks (anchoring when missing) a chain in its own committed transaction.</summary>
    public async Task<LicenseAgentChainCursor> AnchorAsync(string? tenantId, string? dayZone = null)
    {
        await using var conn = await Ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var cursor = await Ledger.LockAsync(conn, tx, tenantId, CancellationToken.None, dayZone);
        await tx.CommitAsync();
        return cursor;
    }

    /// <summary>Makes every non-anchor ledger insert fail, the way a failed ledger write would.</summary>
    public Task FailLedgerInsertsAsync() =>
        ExecAsync(
            "CREATE OR REPLACE FUNCTION test_fail_ledger() RETURNS trigger LANGUAGE plpgsql AS $$ " +
            "BEGIN RAISE EXCEPTION 'injected licensed-agent ledger failure'; END $$; " +
            "CREATE TRIGGER test_fail_ledger BEFORE INSERT ON license_agent_events FOR EACH ROW " +
            "WHEN (NEW.kind NOT IN ('chain_anchored', 'agent_baseline')) EXECUTE FUNCTION test_fail_ledger();");

    public async Task ExecAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = Ds.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }
}
