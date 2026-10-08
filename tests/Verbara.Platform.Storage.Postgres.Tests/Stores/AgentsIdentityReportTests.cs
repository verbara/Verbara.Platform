using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// licensed-agent-metering slice 1 (tasks.md 1.5): <c>scripts/ops/agents-identity-report.sql</c> run against
/// a database seeded with duplicate agents, an orphan agent and a Customer tenant whose parent is null —
/// it lists all of them and modifies nothing. The psql meta-commands (<c>\echo</c>) are stripped; the
/// SQL is the shipped file's.
/// </summary>
public sealed class AgentsIdentityReportTests : IClassFixture<AgentIdentityFixture>
{
    private readonly AgentIdentityFixture _fixture;

    public AgentsIdentityReportTests(AgentIdentityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AgentsIdentityReport_ShouldListDuplicatesOrphansAndOrphanedCustomers_WithoutModifyingData()
    {
        var ds = await _fixture.CreateDatabaseAsync(before018: true);
        await AgentIdentityFixture.InsertTenantAsync(ds, "host", type: 0, parent: null);
        await AgentIdentityFixture.InsertTenantAsync(ds, "cust-ok", type: 2, parent: "host");
        await AgentIdentityFixture.InsertTenantAsync(ds, "cust-null-parent", type: 2, parent: null);
        await AgentIdentityFixture.InsertTenantAsync(ds, "cust-missing-parent", type: 2, parent: "gone");
        await AgentIdentityFixture.InsertUserAsync(ds, "cust-ok", "user-dup", status: 0);
        await AgentIdentityFixture.InsertAgentAsync(ds, "cust-ok", "agent-old", "user-dup",
            createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await AgentIdentityFixture.InsertAgentAsync(ds, "cust-ok", "agent-ext", "user-dup", extension: "2001",
            createdAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        await AgentIdentityFixture.InsertAgentAsync(ds, "cust-ok", "agent-orphan", "user-gone");
        var before = await FingerprintAsync(ds);

        var sections = await RunReportAsync(ds);

        sections.Should().HaveCount(4);
        var duplicates = sections[0];
        duplicates.Select(r => r["agent_id"]).Should().BeEquivalentTo(["agent-ext", "agent-old"]);
        duplicates.Single(r => r["agent_id"] == "agent-ext")["proposal"].Should().Be("keep", "the agent with the extension is kept");
        duplicates.Single(r => r["agent_id"] == "agent-old")["proposal"].Should().Be("loser");
        sections[1].Select(r => r["agent_id"]).Should().BeEquivalentTo(["agent-orphan"]);
        sections[2].Select(r => r["tenant_id"]).Should().BeEquivalentTo(["cust-null-parent", "cust-missing-parent"]);
        (await FingerprintAsync(ds)).Should().Be(before, "the report only reads");
    }

    private static async Task<List<List<Dictionary<string, string>>>> RunReportAsync(NpgsqlDataSource ds)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "ops", "agents-identity-report.sql"));
        var sql = string.Join('\n', File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith('\\')));

        var sections = new List<List<Dictionary<string, string>>>();
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 0)
                continue;
            var rows = new List<Dictionary<string, string>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
                rows.Add(row);
            }
            sections.Add(rows);
        }
        while (await reader.NextResultAsync());
        return sections;
    }

    private static async Task<string> FingerprintAsync(NpgsqlDataSource ds)
    {
        await using var cmd = ds.CreateCommand(
            "SELECT (SELECT string_agg(agent_id || ':' || user_id, ',' ORDER BY agent_id) FROM agents) || '/' || " +
            "(SELECT string_agg(tenant_id || ':' || COALESCE(parent_tenant_id, ''), ',' ORDER BY tenant_id) FROM tenants) || '/' || " +
            "(SELECT string_agg(user_id, ',' ORDER BY user_id) FROM users)");
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
