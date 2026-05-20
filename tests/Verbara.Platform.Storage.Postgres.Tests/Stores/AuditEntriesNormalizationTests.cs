using System.Text.Json;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Npgsql;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Integration coverage for the audit_entries schema normalization shipped in
/// migration 021 (R5.3 Phase A Task A.1, ADR-0006). Validates that:
///
///   1. The writer persists Category / Severity / ActorType / Changes
///      (Before+After) / IntegrityHash to typed columns instead of silently
///      losing them into the legacy `details` JSONB blob.
///   2. CHECK constraints reject invalid severity / category values.
///   3. The reader hydrates AuditEntry.Changes from the typed
///      before_json / after_json columns.
///   4. Severity-filter and category-filter queries hit the new
///      idx_audit_severity / idx_audit_category B-tree indexes (verified via
///      EXPLAIN — no sequential scan).
/// </summary>
[Collection("AuditEntriesNormalization")]
public sealed class AuditEntriesNormalizationTests
{
    private readonly AuditEntriesNormalizationFixture _fixture;

    public AuditEntriesNormalizationTests(AuditEntriesNormalizationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Insert_ShouldPersistTypedColumns_WhenAuditEntryHasCategoryAndSeverity()
    {
        await _fixture.ResetAsync();
        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = new PostgresAuditStore(dataSource);
            var entry = NewEntry(
                tenantId: "tenant-a",
                category: "security",
                severity: "critical",
                actorType: "user",
                actorId: "alice",
                changes: new AuditChanges(
                    Before: new { Status = "Active" },
                    After: new { Status = "Suspended" }),
                integrityHash: "sha256:deadbeef");

            await store.SaveAsync(entry, CancellationToken.None);

            // Direct DB read — bypass the row mapper to confirm columns are
            // populated rather than tunneled through the details blob.
            await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT category, severity, actor_type, before_json::text, after_json::text, integrity_hash " +
                "FROM audit_entries WHERE entry_id = @EntryId", conn);
            cmd.Parameters.Add(new NpgsqlParameter("EntryId", entry.EntryId.Value));
            await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();

            var category = reader.GetString("category");
            var severity = reader.GetString("severity");
            var actorType = reader.GetString("actor_type");
            var beforeJson = reader.GetStringOrNull("before_json");
            var afterJson = reader.GetStringOrNull("after_json");
            var integrityHash = reader.GetStringOrNull("integrity_hash");

            category.Should().Be("security");
            severity.Should().Be("critical");
            actorType.Should().Be("user");
            integrityHash.Should().Be("sha256:deadbeef");
            beforeJson.Should().NotBeNull();
            // Postgres canonicalises jsonb with a space after colons — assert
            // on the property + value tokens independently to stay tolerant of
            // formatter changes.
            beforeJson!.Should().Contain("\"status\"");
            beforeJson.Should().Contain("\"Active\"");
            afterJson.Should().NotBeNull();
            afterJson!.Should().Contain("\"status\"");
            afterJson.Should().Contain("\"Suspended\"");
        }
    }

    [Fact]
    public async Task Query_ShouldFilterBySeverity_UsingIndex()
    {
        await _fixture.ResetAsync();
        await SeedRowsForExplainAsync();

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        // Drop competing indexes for this session so the planner is forced to
        // choose between the new (tenant_id, severity, occurred_at) index and
        // a sequential scan. enable_seqscan=off then guarantees the index
        // wins. We keep the changes session-scoped so other tests are
        // unaffected — the truncate in ResetAsync() doesn't drop indexes.
        await ExecAsync(conn, "DROP INDEX IF EXISTS idx_audit_time");
        await ExecAsync(conn, "DROP INDEX IF EXISTS idx_audit_entity");
        await ExecAsync(conn, "DROP INDEX IF EXISTS idx_audit_category");
        // Use session-level SET (not SET LOCAL) — there is no surrounding
        // transaction, so SET LOCAL would be a no-op. Pooling is also off
        // (Npgsql default per-connection here) so this leaks no global state.
        await ExecAsync(conn, "SET enable_seqscan = off");

        // EXPLAIN returns one row per plan line — join into a single string
        // so Contains() can search the whole plan.
        var planLines = await ExplainAsync(conn,
            "EXPLAIN (FORMAT TEXT) " +
            "SELECT * FROM audit_entries WHERE tenant_id = 'tenant-explain' AND severity = 'critical' " +
            "ORDER BY occurred_at DESC LIMIT 50");
        var plan = string.Join("\n", planLines);

        plan.Should().Contain("idx_audit_severity",
            because: $"the (tenant_id, severity, occurred_at DESC) index should serve this filter — actual plan was:\n{plan}");

        // Restore indexes for other tests.
        await ExecAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_audit_time ON audit_entries (tenant_id, occurred_at DESC); " +
            "CREATE INDEX IF NOT EXISTS idx_audit_entity ON audit_entries (tenant_id, entity_type, entity_id, occurred_at DESC); " +
            "CREATE INDEX IF NOT EXISTS idx_audit_category ON audit_entries (tenant_id, category, occurred_at DESC)");
    }

    [Fact]
    public async Task Query_ShouldFilterByCategory_UsingIndex()
    {
        await _fixture.ResetAsync();
        await SeedRowsForExplainAsync();

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await ExecAsync(conn, "DROP INDEX IF EXISTS idx_audit_time");
        await ExecAsync(conn, "DROP INDEX IF EXISTS idx_audit_entity");
        await ExecAsync(conn, "DROP INDEX IF EXISTS idx_audit_severity");
        // Use session-level SET (not SET LOCAL) — there is no surrounding
        // transaction, so SET LOCAL would be a no-op. Pooling is also off
        // (Npgsql default per-connection here) so this leaks no global state.
        await ExecAsync(conn, "SET enable_seqscan = off");

        var planLines = await ExplainAsync(conn,
            "EXPLAIN (FORMAT TEXT) " +
            "SELECT * FROM audit_entries WHERE tenant_id = 'tenant-explain' AND category = 'security' " +
            "ORDER BY occurred_at DESC LIMIT 50");
        var plan = string.Join("\n", planLines);

        plan.Should().Contain("idx_audit_category",
            because: $"the (tenant_id, category, occurred_at DESC) index should serve this filter — actual plan was:\n{plan}");

        // Restore indexes for other tests.
        await ExecAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_audit_time ON audit_entries (tenant_id, occurred_at DESC); " +
            "CREATE INDEX IF NOT EXISTS idx_audit_entity ON audit_entries (tenant_id, entity_type, entity_id, occurred_at DESC); " +
            "CREATE INDEX IF NOT EXISTS idx_audit_severity ON audit_entries (tenant_id, severity, occurred_at DESC)");
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ExplainAsync(NpgsqlConnection conn, string sql)
    {
        var lines = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            lines.Add(reader.GetString(0));
        return lines;
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("INVALID")]
    [InlineData("")]
    public async Task CheckConstraint_ShouldRejectInvalidSeverity_WhenWritten(string invalid)
    {
        await _fixture.ResetAsync();

        var act = async () => await _fixture.ExecAsync(
            "INSERT INTO audit_entries (entry_id, tenant_id, action, entity_type, entity_id, " +
            "occurred_at, category, severity, actor_type) " +
            $"VALUES ('e1', 't1', 'a', 'e', '1', NOW(), 'config', '{invalid}', 'system')");

        await act.Should().ThrowAsync<PostgresException>()
            .Where(ex => ex.SqlState == "23514"); // check_violation
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("INVALID")]
    [InlineData("")]
    public async Task CheckConstraint_ShouldRejectInvalidCategory_WhenWritten(string invalid)
    {
        await _fixture.ResetAsync();

        var act = async () => await _fixture.ExecAsync(
            "INSERT INTO audit_entries (entry_id, tenant_id, action, entity_type, entity_id, " +
            "occurred_at, category, severity, actor_type) " +
            $"VALUES ('e1', 't1', 'a', 'e', '1', NOW(), '{invalid}', 'info', 'system')");

        await act.Should().ThrowAsync<PostgresException>()
            .Where(ex => ex.SqlState == "23514"); // check_violation
    }

    [Fact]
    public async Task Reader_ShouldHydrateChanges_FromBeforeAfterColumns()
    {
        await _fixture.ResetAsync();
        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = new PostgresAuditStore(dataSource);
            var entry = NewEntry(
                tenantId: "tenant-r",
                category: "config",
                severity: "warn",
                actorType: "user",
                actorId: "bob",
                changes: new AuditChanges(
                    Before: new { Threshold = 5, Enabled = false },
                    After: new { Threshold = 10, Enabled = true }));

            await store.SaveAsync(entry, CancellationToken.None);

            var rehydrated = await store.GetByEntityAsync(
                entry.TenantId, entry.TargetType!, entry.TargetId!, CancellationToken.None);

            rehydrated.Should().HaveCount(1);
            var got = rehydrated[0];

            got.Category.Should().Be("config");
            got.Severity.Should().Be("warn");
            got.ActorType.Should().Be("user");
            got.Changes.Should().NotBeNull();

            // Before / After are surfaced as JsonElement (parsed from
            // before_json / after_json columns by the row mapper).
            got.Changes!.Before.Should().BeOfType<JsonElement>();
            got.Changes.After.Should().BeOfType<JsonElement>();

            var beforeEl = (JsonElement)got.Changes.Before!;
            beforeEl.GetProperty("threshold").GetInt32().Should().Be(5);
            beforeEl.GetProperty("enabled").GetBoolean().Should().BeFalse();

            var afterEl = (JsonElement)got.Changes.After!;
            afterEl.GetProperty("threshold").GetInt32().Should().Be(10);
            afterEl.GetProperty("enabled").GetBoolean().Should().BeTrue();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static AuditEntry NewEntry(
        string tenantId,
        string category,
        string severity,
        string actorType,
        string actorId,
        AuditChanges? changes = null,
        string? integrityHash = null) =>
        new()
        {
            EntryId = EntityId.New(),
            TenantId = new TenantId(tenantId),
            Action = "test.action",
            Category = category,
            Severity = severity,
            ActorId = actorId,
            ActorType = actorType,
            TargetType = "TestEntity",
            TargetId = "tgt-" + Guid.NewGuid().ToString("N")[..8],
            Changes = changes,
            IntegrityHash = integrityHash,
            OccurredAt = DateTimeOffset.UtcNow,
        };

    private async Task SeedRowsForExplainAsync()
    {
        // Seed enough rows so the planner has stats — index choice is more
        // deterministic with > 100 rows even with seq-scan disabled.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_entries (entry_id, tenant_id, action, entity_type, entity_id,
                                       occurred_at, category, severity, actor_type)
            SELECT 'row-' || gs::text,
                   'tenant-explain',
                   'a',
                   'e',
                   '1',
                   NOW() - (gs || ' seconds')::interval,
                   CASE (gs % 4)
                       WHEN 0 THEN 'security'
                       WHEN 1 THEN 'config'
                       WHEN 2 THEN 'auth'
                       ELSE 'admin'
                   END,
                   CASE (gs % 4)
                       WHEN 0 THEN 'critical'
                       WHEN 1 THEN 'info'
                       WHEN 2 THEN 'warn'
                       ELSE 'error'
                   END,
                   'system'
            FROM generate_series(1, 200) gs
            """;
        await cmd.ExecuteNonQueryAsync();

        await using var analyze = conn.CreateCommand();
        analyze.CommandText = "ANALYZE audit_entries";
        await analyze.ExecuteNonQueryAsync();
    }
}
