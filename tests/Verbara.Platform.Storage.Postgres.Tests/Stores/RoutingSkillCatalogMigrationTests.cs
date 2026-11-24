using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// routing-skills-stay-in-their-tenant (design D2, task 2.1): migration 032 creates the tenant-keyed routing skill
/// tables, the last migration, idempotent. On <see cref="PinnedPostgres18MigrationsFixture"/>, the image the
/// statements were measured on; <see cref="MigrationsTests"/> applies the same file on <c>postgres:16-alpine</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RoutingSkillCatalogMigrationTests : IClassFixture<PinnedPostgres18MigrationsFixture>
{
    private const string Prefix = "Verbara.Platform.Storage.Postgres.Migrations.";

    private readonly PinnedPostgres18MigrationsFixture _fixture;

    public RoutingSkillCatalogMigrationTests(PinnedPostgres18MigrationsFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration032_ShouldCreateTheRoutingSkillTables_AndBeIdempotent()
    {
        var names = typeof(ServiceCollectionExtensions).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        names[^1].Should().Be(Prefix + "032_RoutingSkillCatalog.sql", "032 is the last migration");
        names.IndexOf(Prefix + "032_RoutingSkillCatalog.sql")
            .Should().Be(names.IndexOf(Prefix + "031_AgentAssistSharedState.sql") + 1, "032 runs right after 031");
        names.Should().ContainSingle(n => n.Contains(".032_", StringComparison.Ordinal), "one migration per ordinal");

        await AssertRoutingSkillTablesAsync();

        await _fixture.ReapplyMigrationAsync("032_RoutingSkillCatalog.sql");

        await AssertRoutingSkillTablesAsync();
    }

    private async Task AssertRoutingSkillTablesAsync()
    {
        (await _fixture.TableExistsAsync("routing_skills")).Should().BeTrue();
        (await _fixture.TableExistsAsync("agent_skill_proficiencies")).Should().BeTrue();

        (await _fixture.ColumnDefinitionAsync("routing_skills", "name")).Should().Be(("text", false));
        (await _fixture.ColumnDefinitionAsync("routing_skills", "category")).Should().Be(("text", true));
        (await _fixture.ColumnDefinitionAsync("routing_skills", "description")).Should().Be(("text", true));
        (await _fixture.ColumnDefinitionAsync("agent_skill_proficiencies", "proficiency")).Should().Be(("integer", false));

        (await UniqueIndexDefinitionAsync("ux_routing_skills_tenant_name"))
            .Should().Be("CREATE UNIQUE INDEX ux_routing_skills_tenant_name ON public.routing_skills USING btree (tenant_id, lower(name))");
        (await UniqueIndexDefinitionAsync("ux_agent_skill_proficiencies_key"))
            .Should().Be("CREATE UNIQUE INDEX ux_agent_skill_proficiencies_key ON public.agent_skill_proficiencies " +
                         "USING btree (tenant_id, agent_id, lower(skill_name))");

        (await ForeignKeyDefinitionAsync("fk_agent_skill_proficiencies_agent"))
            .Should().Be("FOREIGN KEY (tenant_id, agent_id) REFERENCES agents(tenant_id, agent_id) ON DELETE CASCADE");
        (await ForeignKeyCountAsync("agent_skill_proficiencies"))
            .Should().Be(1, "an agent may hold a skill the catalog does not define, so no key points at routing_skills");
    }

    private async Task<string?> UniqueIndexDefinitionAsync(string indexName)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT pg_get_indexdef(i.indexrelid) FROM pg_index i " +
            "WHERE i.indexrelid = to_regclass(@Name) AND i.indisunique";
        cmd.Parameters.Add(new NpgsqlParameter("Name", indexName));
        return await cmd.ExecuteScalarAsync() as string;
    }

    private async Task<string?> ForeignKeyDefinitionAsync(string constraintName)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = @Name AND contype = 'f'";
        cmd.Parameters.Add(new NpgsqlParameter("Name", constraintName));
        return await cmd.ExecuteScalarAsync() as string;
    }

    private async Task<long> ForeignKeyCountAsync(string table)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pg_constraint WHERE conrelid = to_regclass(@Table) AND contype = 'f'";
        cmd.Parameters.Add(new NpgsqlParameter("Table", table));
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
