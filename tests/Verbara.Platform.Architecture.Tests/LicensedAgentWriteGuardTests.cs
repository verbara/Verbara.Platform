namespace Verbara.Platform.Architecture.Tests;

/// <summary>
/// licensed-agent-metering (tasks.md 5.5, design D5): every SQL writer of <c>agents</c> and
/// <c>users.status</c> is reached only through the licensed-agent writer, so no counted change commits
/// without its ledger row. The real-tree tests run <see cref="LicensedAgentWriteScanner"/> over
/// <c>src/</c>; the probes pin each arm and each allowlist entry (a probe per entry: an allowlist that only
/// fixes membership would ratify a hole).
/// </summary>
public sealed class LicensedAgentWriteGuardTests
{
    private const string StorageProject = "Verbara.Platform.Storage.Postgres";

    [Fact]
    public void StorageSqlWriters_ShouldBeReachedOnlyThroughTheLicensedAgentWriter()
    {
        var files = Sources(Path.Combine(SourceTreeSource.SrcRoot(), StorageProject)).ToList();
        files.Should().Contain(f => f.File == LicensedAgentWriteScanner.WriterFile, "the scan must reach the writer");
        files.Count.Should().BeGreaterThan(40, "the scan must walk the real Storage.Postgres tree");

        LicensedAgentWriteScanner.ScanStorage(files).Should().BeEmpty();
    }

    [Fact]
    public void StorageSqlWriters_ShouldEachBeAnAllowedConstantThatTheScanFinds()
    {
        // Every allowlist entry must still name a constant that holds a counted write: a stale entry would
        // let a future constant of that name through unexamined.
        var files = Sources(Path.Combine(SourceTreeSource.SrcRoot(), StorageProject)).ToDictionary(f => f.File, f => f.Source);
        foreach (var (file, constant) in LicensedAgentWriteScanner.AllowedSqlConstants)
        {
            files.Should().ContainKey(file);
            var mutated = files.Select(f => (f.Key, f.Key == file
                ? f.Value.Replace($" {constant} =", $" {constant}Renamed =", StringComparison.Ordinal)
                : f.Value)).ToList();
            LicensedAgentWriteScanner.ScanStorage(mutated).Should().Contain(
                v => v.Path == file && v.Where == constant + "Renamed",
                $"{file}:{constant} must hold a counted write the scan recognises");
        }
    }

    [Fact]
    public void ProductionCallers_ShouldNotWriteUserStatusOrDeleteThroughTheStoresDirectly()
    {
        var root = SourceTreeSource.SrcRoot();
        var files = Directory.GetDirectories(root)
            .Where(d => !Path.GetFileName(d).StartsWith("Verbara.Platform.Storage.", StringComparison.Ordinal))
            .SelectMany(Sources)
            .ToList();
        files.Should().Contain(f => f.File.EndsWith("AdminEndpoints.cs", StringComparison.Ordinal));

        LicensedAgentWriteScanner.ScanCallers(files).Should().BeEmpty();
    }

    // ─── probes: SQL arm ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("DELETE FROM agents WHERE agent_id = @A")]
    [InlineData("INSERT INTO agents (agent_id) VALUES (@A)")]
    [InlineData("DELETE FROM users WHERE user_id = @U")]
    [InlineData("UPDATE users SET status = @S WHERE user_id = @U")]
    [InlineData("UPDATE users AS u SET display_name = @D, status = COALESCE(@S, u.status) WHERE u.user_id = @U")]
    public void ScanStorage_ShouldFlagInlineCountedSql(string sql)
    {
        var store = $$"""
            internal sealed class SomeStore
            {
                public async Task WriteAsync(TenantId tenantId, CancellationToken ct)
                {
                    await _dataSource.ExecuteAsync("{{sql}}", p => { }, ct);
                }
            }
            """;

        LicensedAgentWriteScanner.ScanStorage([("SomeStore.cs", store), (LicensedAgentWriteScanner.WriterFile, "")])
            .Should().ContainSingle(v => v.Where == "WriteAsync");
    }

    [Theory]
    [InlineData("SELECT agent_id FROM agents WHERE user_id = @U FOR UPDATE")]
    [InlineData("UPDATE users SET password_hash = @H WHERE user_id = @U")]
    [InlineData("DELETE FROM agent_capacity WHERE agent_id = @A")]
    [InlineData("SELECT status FROM users WHERE tenant_id = @T AND user_id = @U FOR UPDATE")]
    public void ScanStorage_ShouldNotFlagSqlThatChangesNoCount(string sql)
    {
        var store = $$"""
            internal sealed class SomeStore
            {
                public Task WriteAsync(CancellationToken ct) => _dataSource.ExecuteAsync("{{sql}}", p => { }, ct);
            }
            """;

        LicensedAgentWriteScanner.ScanStorage([("SomeStore.cs", store)]).Should().BeEmpty();
    }

    [Fact]
    public void ScanStorage_ShouldFlagAnAllowedConstantUsedOutsideATransactionOverload()
    {
        var store = """
            internal sealed class PostgresAgentStore
            {
                internal const string InsertSql = "INSERT INTO agents (agent_id) VALUES (@A)";

                internal static Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Agent agent, CancellationToken ct) =>
                    conn.ExecuteAsync(InsertSql, p => { }, tx, ct);

                public Task SaveAsync(Agent agent, CancellationToken ct) => _dataSource.ExecuteAsync(InsertSql, p => { }, ct);
            }
            """;
        const string writer = "await PostgresAgentStore.InsertAsync(conn, tx, agent, ct);";

        LicensedAgentWriteScanner.ScanStorage([("PostgresAgentStore.cs", store), (LicensedAgentWriteScanner.WriterFile, writer)])
            .Should().ContainSingle().Which.Where.Should().Be("SaveAsync");
    }

    [Fact]
    public void ScanStorage_ShouldFlagATransactionOverloadTheWriterNeverCalls_AndAnyOtherCaller()
    {
        var store = """
            internal sealed class PostgresUserStore
            {
                internal const string DeleteSql = "DELETE FROM users WHERE user_id = @U";

                internal static Task<int> DeleteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantId t, EntityId u, CancellationToken ct) =>
                    conn.ExecuteAsync(DeleteSql, p => { }, tx, ct);
            }
            """;
        const string other = "await PostgresUserStore.DeleteAsync(conn, tx, tenantId, userId, ct);";

        var violations = LicensedAgentWriteScanner.ScanStorage(
            [("PostgresUserStore.cs", store), ("SomeOtherStore.cs", other), (LicensedAgentWriteScanner.WriterFile, "")]);

        violations.Should().Contain(v => v.Path == "PostgresUserStore.cs" && v.Why.Contains("never calls"));
        violations.Should().Contain(v => v.Path == "SomeOtherStore.cs");
    }

    [Fact]
    public void ScanStorage_ShouldFlagACountedSqlConstantThatIsNotAllowed()
    {
        var store = """
            internal sealed class PostgresAgentStore
            {
                internal const string PurgeSql = "DELETE FROM agents WHERE tenant_id = @T";
            }
            """;

        LicensedAgentWriteScanner.ScanStorage([("PostgresAgentStore.cs", store)])
            .Should().ContainSingle().Which.Where.Should().Be("PurgeSql");
    }

    // ─── probes: caller arm ────────────────────────────────────────────────────

    [Theory]
    [InlineData("IUserStore store", "store.UpdateAdminFieldsAsync(t, u, change, now, by, ct)")]
    [InlineData("IUserStore users", "users.DeleteAsync(t, u, ct)")]
    [InlineData("IAgentStore agents", "agents.DeleteAsync(t, a, ct)")]
    [InlineData("IAgentStore? agents", "agents . DeleteAsync(t, a, ct)")]
    public void ScanCallers_ShouldFlagADirectStoreWrite(string parameter, string call)
    {
        var endpoint = $$"""
            internal static class SomeEndpoints
            {
                private static async Task<IResult> Handle(string id, [FromServices] {{parameter}}, CancellationToken ct)
                {
                    await {{call}};
                    return Results.NoContent();
                }
            }
            """;

        LicensedAgentWriteScanner.ScanCallers([("SomeEndpoints.cs", endpoint)]).Should().ContainSingle(v => v.Where == "Handle");
    }

    [Fact]
    public void ScanCallers_ShouldFlagAFieldTypedStore_AndNotAnotherStoreSharingTheName()
    {
        var service = """
            internal sealed class SomeService
            {
                private readonly IAgentStore _agentStore;

                public async Task PurgeAsync(CancellationToken ct) => await _agentStore.DeleteAsync(t, a, ct);

                private static async Task DeleteQueue([FromServices] IQueueStore store, CancellationToken ct) =>
                    await store.DeleteAsync(t, q, ct);

                private static async Task ReadAgent([FromServices] IAgentStore store, CancellationToken ct) =>
                    await store.GetByIdAsync(t, a, ct);
            }
            """;

        LicensedAgentWriteScanner.ScanCallers([("SomeService.cs", service)])
            .Should().ContainSingle().Which.Where.Should().Be("PurgeAsync");
    }

    [Theory]
    [InlineData("CachedUserStore.cs")]
    [InlineData("RealtimeSyncingAgentStore.cs")]
    public void ScanCallers_ShouldLetOnlyTheNamedDecoratorsForwardToTheirInnerStore(string decorator)
    {
        var source = """
            internal sealed class Decorator
            {
                private readonly IUserStore _inner;
                private readonly IAgentStore _innerAgents;
                public Task<bool> DeleteAsync(TenantId t, EntityId u, CancellationToken ct) => _inner.DeleteAsync(t, u, ct);
                public Task Delete2Async(TenantId t, EntityId a, CancellationToken ct) => _innerAgents.DeleteAsync(t, a, ct);
            }
            """;

        LicensedAgentWriteScanner.ScanCallers([(decorator, source)]).Should().BeEmpty();
        LicensedAgentWriteScanner.ScanCallers([("NotADecorator.cs", source)]).Should().HaveCount(2);
    }

    private static IEnumerable<(string File, string Source)> Sources(string projectRoot) =>
        Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));
}
