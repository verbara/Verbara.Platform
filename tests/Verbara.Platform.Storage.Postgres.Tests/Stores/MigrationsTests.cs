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
}
