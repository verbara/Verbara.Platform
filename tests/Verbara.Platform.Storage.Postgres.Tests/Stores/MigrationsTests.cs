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
}
