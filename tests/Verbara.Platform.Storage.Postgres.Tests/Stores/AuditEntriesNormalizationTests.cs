using System.Text.Json;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Npgsql;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Integration coverage for the audit_entries schema normalization (ADR-0006),
/// now folded into the consolidated 001_Baseline.sql. Validates that:
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

    [Theory]
    // The full vocabulary the application code actually emits via IAuditService.RecordAsync.
    // 'conversations', 'queues', 'reports', 'operational', 'license' were missing from the
    // migration 021 CHECK and so failed with 23514 against Postgres (the W3-W6 liveness /
    // deferred-pause / work-failover / callback-rescue workers emit 'queues'/'conversations').
    // Migration 034 widens the constraint to the real union — this test locks it.
    [InlineData("auth")]
    [InlineData("billing")]
    [InlineData("config")]
    [InlineData("tenant")]
    [InlineData("security")]
    [InlineData("impersonation")]
    [InlineData("retention")]
    [InlineData("data")]
    [InlineData("rbac")]
    [InlineData("data_access")]
    [InlineData("admin")]
    [InlineData("conversations")]
    [InlineData("queues")]
    [InlineData("reports")]
    [InlineData("operational")]
    [InlineData("license")]
    public async Task CheckConstraint_ShouldAcceptDomainCategory_WhenWritten(string category)
    {
        await _fixture.ResetAsync();

        var act = async () => await _fixture.ExecAsync(
            "INSERT INTO audit_entries (entry_id, tenant_id, action, entity_type, entity_id, " +
            "occurred_at, category, severity, actor_type) " +
            $"VALUES ('e1', 't1', 'a', 'e', '1', NOW(), '{category}', 'info', 'system')");

        await act.Should().NotThrowAsync(
            because: $"'{category}' is a category emitted by application code and must satisfy audit_entries_category_check");
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

    // ─── Retention floor (ADR-0034 Decision 4) — live DB ────────────────────────────

    [Fact]
    public async Task DeleteOlderThanAsync_ShouldPreserveRecord_WhenRetainUntilInFuture()
    {
        // Locks the now()-vs-@Cutoff SQL on the real DB: a record whose OccurredAt predates the
        // cutoff but whose retain_until is still in the future MUST survive (retain_until compared
        // to now(), NOT to the past @Cutoff).
        await _fixture.ResetAsync();
        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = new PostgresAuditStore(dataSource);
            var tenant = new TenantId("tenant-floor-a");
            var inWindow = new AuditEntry
            {
                EntryId = EntityId.New(),
                TenantId = tenant,
                Action = AutonomousAuditRedaction.AutonomousCommitAction,
                Category = "conversations",
                Severity = "info",
                ActorId = "verbara:ai:autonomous-worker",
                ActorType = "ai",
                TargetType = "Conversation",
                TargetId = "conv-in-window",
                OccurredAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), // long before cutoff
                RetainUntil = DateTimeOffset.UtcNow.AddYears(1),                     // floor not yet elapsed
            };
            await store.SaveAsync(inWindow, CancellationToken.None);

            var deleted = await store.DeleteOlderThanAsync(
                tenant, DateTimeOffset.UtcNow.AddMonths(-1), CancellationToken.None);

            deleted.Should().Be(0);
            var remaining = await store.GetByEntityAsync(tenant, "Conversation", "conv-in-window", CancellationToken.None);
            remaining.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task DeleteOlderThanAsync_ShouldPurgeRecord_WhenRetainUntilElapsedOrNull()
    {
        // Two rows past the cutoff: one with an elapsed floor, one with no floor — both purge.
        await _fixture.ResetAsync();
        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = new PostgresAuditStore(dataSource);
            var tenant = new TenantId("tenant-floor-b");

            var pastFloor = new AuditEntry
            {
                EntryId = EntityId.New(),
                TenantId = tenant,
                Action = AutonomousAuditRedaction.AutonomousCommitAction,
                Category = "conversations",
                Severity = "info",
                ActorId = "verbara:ai:autonomous-worker",
                ActorType = "ai",
                TargetType = "Conversation",
                TargetId = "conv-past-floor",
                OccurredAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                RetainUntil = DateTimeOffset.UtcNow.AddDays(-1), // floor already elapsed
            };
            var noFloor = new AuditEntry
            {
                EntryId = EntityId.New(),
                TenantId = tenant,
                Action = "conversation.created",
                Category = "conversations",
                Severity = "info",
                ActorId = "system",
                ActorType = "system",
                TargetType = "Conversation",
                TargetId = "conv-no-floor",
                OccurredAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), // RetainUntil null
            };
            await store.SaveAsync(pastFloor, CancellationToken.None);
            await store.SaveAsync(noFloor, CancellationToken.None);

            var deleted = await store.DeleteOlderThanAsync(
                tenant, DateTimeOffset.UtcNow.AddMonths(-1), CancellationToken.None);

            deleted.Should().Be(2);
            (await store.GetByEntityAsync(tenant, "Conversation", "conv-past-floor", CancellationToken.None))
                .Should().BeEmpty();
            (await store.GetByEntityAsync(tenant, "Conversation", "conv-no-floor", CancellationToken.None))
                .Should().BeEmpty();
        }
    }

    // ─── Art. 17 redaction (ADR-0034 Decision 4) — live DB ──────────────────────────

    [Fact]
    public async Task RedactContactLinkageAsync_ShouldNullLinkageButRetainDecisionFact_WhenAutonomousRecord()
    {
        // Locks the entity_id = ANY(@ConversationIds) match, the entity_id → NULL update (requires
        // the migration-014 NOT NULL drop), the jsonb metadata tombstone, and the hash round-trip on
        // the real DB. A non-autonomous record with the same conversation id must be untouched.
        await _fixture.ResetAsync();
        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = new PostgresAuditStore(dataSource);
            var tenant = new TenantId("tenant-erase");

            var autonomous = new AuditEntry
            {
                EntryId = EntityId.New(),
                TenantId = tenant,
                Action = AutonomousAuditRedaction.AutonomousCommitAction,
                Category = "conversations",
                Severity = "info",
                ActorId = "verbara:ai:autonomous-worker",
                ActorType = "ai",
                TargetType = "Conversation",
                TargetId = "conv-erase",
                OccurredAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                RetainUntil = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                Metadata = new Dictionary<string, string>
                {
                    ["node_path"] = "Sales > Upgrade > Completed",
                    ["leaf_node_id"] = "leaf-1",
                    ["confidence"] = "0.9800",
                    ["tenant"] = tenant.Value,
                    ["conversation"] = "conv-erase",
                },
            };
            // A non-autonomous record referencing the SAME conversation id must be left untouched.
            var nonAutonomous = new AuditEntry
            {
                EntryId = EntityId.New(),
                TenantId = tenant,
                Action = "conversation.created",
                Category = "conversations",
                Severity = "info",
                ActorId = "system",
                ActorType = "system",
                TargetType = "Conversation",
                TargetId = "conv-erase",
                OccurredAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            };
            await store.SaveAsync(autonomous, CancellationToken.None);
            await store.SaveAsync(nonAutonomous, CancellationToken.None);

            var redacted = await store.RedactContactLinkageAsync(
                tenant, ["conv-erase"], CancellationToken.None);

            redacted.Should().Be(1);

            // The decision fact survives, queryable by node path / confidence; the contact linkage is gone.
            var commits = await store.SearchAsync(
                tenant, new AuditQuery(Action: AutonomousAuditRedaction.AutonomousCommitAction), CancellationToken.None);
            commits.Items.Should().ContainSingle();
            var result = commits.Items[0];
            result.TargetId.Should().BeNull("the contact-identifying conversation linkage is redacted");
            result.Metadata!["conversation"].Should().Be("[redacted]");
            result.Metadata!["redacted"].Should().Be("true");
            result.Metadata!["node_path"].Should().Be("Sales > Upgrade > Completed", "the decision fact is retained");
            result.Metadata!["confidence"].Should().Be("0.9800");
            result.ActorType.Should().Be("ai");
            result.OccurredAt.Should().Be(autonomous.OccurredAt);
            result.RetainUntil.Should().Be(autonomous.RetainUntil);
            result.IntegrityHash.Should().NotBeNullOrEmpty();

            // The non-autonomous record with the same conversation id is untouched.
            var created = await store.SearchAsync(
                tenant, new AuditQuery(Action: "conversation.created"), CancellationToken.None);
            created.Items.Should().ContainSingle();
            created.Items[0].TargetId.Should().Be("conv-erase", "non-autonomous records are untouched");
        }
    }

    [Fact]
    public async Task RedactContactLinkageAsync_ShouldBeIdempotent_WhenRunTwice()
    {
        await _fixture.ResetAsync();
        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = new PostgresAuditStore(dataSource);
            var tenant = new TenantId("tenant-erase-idem");
            var entry = new AuditEntry
            {
                EntryId = EntityId.New(),
                TenantId = tenant,
                Action = AutonomousAuditRedaction.AutonomousCommitAction,
                Category = "conversations",
                Severity = "info",
                ActorId = "verbara:ai:autonomous-worker",
                ActorType = "ai",
                TargetType = "Conversation",
                TargetId = "conv-erase",
                OccurredAt = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string> { ["conversation"] = "conv-erase" },
            };
            await store.SaveAsync(entry, CancellationToken.None);

            var first = await store.RedactContactLinkageAsync(tenant, ["conv-erase"], CancellationToken.None);
            var second = await store.RedactContactLinkageAsync(tenant, ["conv-erase"], CancellationToken.None);

            first.Should().Be(1);
            second.Should().Be(0, "an already-redacted record (TargetId already null) is not redacted again");
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
