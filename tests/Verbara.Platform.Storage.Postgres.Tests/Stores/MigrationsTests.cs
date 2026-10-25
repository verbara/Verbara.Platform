namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Applies the REAL embedded migration files to a clean Postgres and asserts the
/// resulting schema. Catches drift between the migration SQL and the store
/// expectations that the inlined-DDL store fixtures cannot.
/// </summary>
public class MigrationsTests : IClassFixture<MigrationsFixture>
{
    private readonly MigrationsFixture _fixture;

    public MigrationsTests(MigrationsFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migrations_ShouldCreateReasonHintsTable_WhenAppliedToEmptyDatabase()
    {
        (await _fixture.TableExistsAsync("reason_hints")).Should().BeTrue();
        (await _fixture.IndexExistsAsync("idx_reason_hints_scope")).Should().BeTrue();
    }

    [Fact]
    public async Task Migrations_ShouldAddCreatedAtColumns_WhenApplied()
    {
        (await _fixture.ColumnExistsAsync("typification_bindings", "created_at")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("reason_hints", "created_at")).Should().BeTrue();
    }

    [Fact]
    public async Task Migrations_ShouldAddAutonomousDispositionColumns_WhenApplied()
    {
        (await _fixture.ColumnExistsAsync("typification_submissions", "autonomous_actor_id")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("typification_submissions", "correction_state")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("typification_submissions", "corrected_at")).Should().BeTrue();
    }

    [Fact]
    public async Task Migrations_ShouldCreateTenantAutonomousDispositionTable_WhenApplied()
    {
        (await _fixture.TableExistsAsync("tenant_autonomous_disposition")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("tenant_autonomous_disposition", "attested_by_user_id")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("tenant_autonomous_disposition", "revoked_at")).Should().BeTrue();
    }

    [Fact]
    public async Task Migrations_ShouldAddAuditRetainUntilColumn_WhenApplied()
    {
        (await _fixture.ColumnExistsAsync("audit_entries", "retain_until")).Should().BeTrue();
    }

    // ── csat-runner Phase A (migration 016) ────────────────────────────────────

    [Fact]
    public async Task Migrations_ShouldAddSurveyResponsesCsatColumns_WhenApplied()
    {
        (await _fixture.ColumnExistsAsync("survey_responses", "channel")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("survey_responses", "queue_name")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("survey_responses", "rating")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("survey_responses", "comment")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("survey_responses", "captured_at")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("survey_responses", "call_id")).Should().BeTrue();
    }

    [Fact]
    public async Task Migrations_ShouldCreateSurveyResponsesPartialIndexes_WhenApplied()
    {
        (await _fixture.IndexExistsAsync("idx_survey_resp_queue_captured")).Should().BeTrue();
        (await _fixture.IndexExistsAsync("idx_survey_resp_agent_captured")).Should().BeTrue();
    }

    [Fact]
    public async Task Migrations_ShouldCreateCsatPendingDispatchesTable_WhenApplied()
    {
        (await _fixture.TableExistsAsync("csat_pending_dispatches")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("csat_pending_dispatches", "correlator")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("csat_pending_dispatches", "consumed_at")).Should().BeTrue();
        (await _fixture.IndexExistsAsync("idx_csat_pending_open")).Should().BeTrue();
    }

    [Fact]
    public async Task Migrations_ShouldAddQueueConfigsCsatColumns_WhenApplied()
    {
        (await _fixture.ColumnExistsAsync("queue_configs", "csat_enabled")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("queue_configs", "csat_channel")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("queue_configs", "csat_prompt_id")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("queue_configs", "csat_sampling_rate")).Should().BeTrue();
    }

    [Fact]
    public async Task Migrations_ShouldCreateCsatTemplatesTable_WhenApplied()
    {
        (await _fixture.TableExistsAsync("csat_templates")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("csat_templates", "locale")).Should().BeTrue();
        (await _fixture.ColumnExistsAsync("csat_templates", "body")).Should().BeTrue();
    }

    // ── whatsapp-works-for-real block B (migration 020) ───────────────────────

    [Fact]
    public async Task Migrations_ShouldReplaceExternalIdIndexWithUniquePartialIndex_WhenApplied()
    {
        (await _fixture.IndexExistsAsync("ux_messages_tenant_external")).Should().BeTrue();
        (await _fixture.IndexExistsAsync("idx_messages_external")).Should().BeFalse();
    }

    // ── webhook-delivery-runs-once (migration 023, design D1) ─────────────────

    [Fact]
    public async Task Migrations_ShouldAddWebhookDeliveryClaimColumns_WhenApplied()
    {
        (await _fixture.ColumnDefinitionAsync("webhook_deliveries", "claim_id"))
            .Should().Be(("character varying(32)", true), "the claim id is a nullable Guid \"N\" (32 hex chars)");
        (await _fixture.ColumnDefinitionAsync("webhook_deliveries", "claim_expires_at"))
            .Should().Be(("timestamp with time zone", true), "the lease expiry is nullable: an unclaimed row has none");
        (await _fixture.IndexExistsAsync("ix_webhook_deliveries_pending"))
            .Should().BeTrue("the claim of the next due delivery runs on the existing partial index (design D3)");

        // Additive and idempotent: a second run is a no-op that leaves both columns as they were.
        await _fixture.ReapplyMigrationAsync("023_WebhookDeliveryClaim.sql");

        (await _fixture.ColumnDefinitionAsync("webhook_deliveries", "claim_id"))
            .Should().Be(("character varying(32)", true));
        (await _fixture.ColumnDefinitionAsync("webhook_deliveries", "claim_expires_at"))
            .Should().Be(("timestamp with time zone", true));
    }

    // dialer-honours-dnc-callbacks-and-agent-dispositions (design D16): the dispatch table, its partial index on the
    // pending rows, and an idempotent re-run.
    [Fact]
    public async Task Migration025_ShouldCreateTheDispatchTableAndItsPendingIndex_AndBeIdempotent()
    {
        (await _fixture.ColumnDefinitionAsync("dialer_disposition_dispatches", "claim_id"))
            .Should().Be(("character varying(32)", true));
        (await _fixture.ColumnDefinitionAsync("dialer_disposition_dispatches", "action_id"))
            .Should().Be(("text", false));
        (await _fixture.IndexExistsAsync("ix_dialer_disposition_dispatches_pending")).Should().BeTrue();

        await _fixture.ReapplyMigrationAsync("025_DialerDispositionDispatches.sql");

        (await _fixture.ColumnDefinitionAsync("dialer_disposition_dispatches", "next_attempt_at"))
            .Should().Be(("timestamp with time zone", false));
    }
}
