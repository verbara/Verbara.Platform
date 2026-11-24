using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests.Routing;

/// <summary>
/// routing-skills-stay-in-their-tenant, requirement "The skill catalog survives a restart and is shared by every
/// replica" (design D2): the real Api host over a Testcontainers PostgreSQL, with the composition root's
/// PostgreSQL skill catalog (not the in-memory one the other suites use). A second host over the same database
/// is the second replica, or the restarted Api once the first is disposed. Each test runs on a database of its own.
/// The rows are also read back by SQL, so a host that kept the catalog in its own memory cannot pass.
/// </summary>
public sealed class SkillCatalogDurabilityLiveDbTests : IClassFixture<SkillCatalogDurabilityLiveDbTests.PostgresContainer>
{
    private const string SkillsPath = "/api/v1/admin/skills";
    private const string AgentsPath = "/api/v1/admin/agents";
    private const string Tenant = AuthenticatedPlatformApiFactory.TestTenantId;

    private readonly PostgresContainer _postgres;

    public SkillCatalogDurabilityLiveDbTests(PostgresContainer postgres) => _postgres = postgres;

    [Fact]
    public async Task ListSkills_ShouldCarryTheSkillAndTheProficiency_WhenTheApiRestarts()
    {
        var connectionString = await _postgres.CreateDatabaseAsync();
        string agentId;
        await using (var first = new Host(connectionString))
        {
            first.Services.GetRequiredService<ISkillCatalogStore>().GetType().Name
                .Should().Be("PostgresSkillCatalogStore", "this suite measures the composed PostgreSQL catalog");
            using var client = first.CreateAuthenticatedClient();
            agentId = await CreateAgentAsync(client, AuthenticatedPlatformApiFactory.TestUserId);
            await CreateSkillAsync(client, "billing", "product");
            await AssignAsync(client, agentId, "billing", 8);
        }

        await using var restarted = new Host(connectionString);
        using var after = restarted.CreateAuthenticatedClient();

        using var scope = new AssertionScope();
        (await SkillNamesAsync(after)).Should().Contain("billing", "the restarted Api reads the catalog from the database");
        (await AgentSkillsAsync(after, agentId)).Should().Equal(
            new Dictionary<string, int> { ["billing"] = 8 }, "agent {0}'s proficiency survives the restart", agentId);
        (await ScalarAsync(connectionString,
            "SELECT proficiency FROM agent_skill_proficiencies WHERE tenant_id = @TenantId AND agent_id = @AgentId",
            agentId)).Should().Be(8);
    }

    [Fact]
    public async Task ListSkills_ShouldCarryTheSkill_WhenAnotherReplicaDefinedIt()
    {
        var connectionString = await _postgres.CreateDatabaseAsync();
        await using var replicaA = new Host(connectionString);
        await using var replicaB = new Host(connectionString);
        using var clientA = replicaA.CreateAuthenticatedClient();
        using var clientB = replicaB.CreateAuthenticatedClient();
        var agentId = await CreateAgentAsync(clientA, AuthenticatedPlatformApiFactory.TestUserId);
        (await SkillNamesAsync(clientB)).Should().NotContain("billing", "nothing is defined yet");

        await CreateSkillAsync(clientA, "billing", "product");
        await AssignAsync(clientA, agentId, "billing", 7);

        using var scope = new AssertionScope();
        (await SkillNamesAsync(clientB)).Should().Contain("billing",
            "a skill defined through one replica is read through the other");
        (await AgentSkillsAsync(clientB, agentId)).Should().Equal(
            new Dictionary<string, int> { ["billing"] = 7 }, "both replicas read the same proficiency of agent {0}", agentId);
        (await AgentsWithSkillAsync(clientB, "billing")).Should().Equal([agentId]);
    }

    [Fact]
    public async Task ListAgentsWithSkill_ShouldNotListTheAgent_WhenTheAgentIsDeleted()
    {
        var connectionString = await _postgres.CreateDatabaseAsync();
        await using var host = new Host(connectionString);
        using var client = host.CreateAuthenticatedClient();
        var deletedAgentId = await CreateAgentAsync(client, AuthenticatedPlatformApiFactory.TestUserId);
        var keptAgentId = await CreateAgentAsync(client, host.SeedActiveUser());
        await CreateSkillAsync(client, "billing", "product");
        await AssignAsync(client, deletedAgentId, "billing", 8);
        await AssignAsync(client, keptAgentId, "billing", 6);

        using (var delete = await client.DeleteAsync($"{AgentsPath}/{deletedAgentId}"))
            delete.StatusCode.Should().Be(HttpStatusCode.NoContent, await delete.Content.ReadAsStringAsync());

        using var scope = new AssertionScope();
        (await AgentsWithSkillAsync(client, "billing")).Should().Equal([keptAgentId],
            "agent {0}'s assignments went with it; agent {1}'s stay", deletedAgentId, keptAgentId);
        using (var skills = await client.GetAsync($"{AgentsPath}/{deletedAgentId}/skills"))
            skills.StatusCode.Should().Be(HttpStatusCode.NotFound, "agent {0} no longer exists", deletedAgentId);
        (await ScalarAsync(connectionString,
            "SELECT count(*) FROM agent_skill_proficiencies WHERE tenant_id = @TenantId AND agent_id = @AgentId",
            deletedAgentId)).Should().Be(0L, "no assignment of agent {0} remains", deletedAgentId);
        (await AgentSkillsAsync(client, keptAgentId)).Should().Equal(new Dictionary<string, int> { ["billing"] = 6 });
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task<string> CreateAgentAsync(HttpClient client, string userId)
    {
        using var response = await client.PostAsJsonAsync(AgentsPath, new { userId, displayName = $"Agent of {userId}" });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        return JsonNode.Parse(body)!["agentId"]!.GetValue<string>();
    }

    private static async Task CreateSkillAsync(HttpClient client, string name, string category)
    {
        using var response = await client.PostAsync(SkillsPath,
            JsonContent.Create(new { name, category, description = default(string) }));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task AssignAsync(HttpClient client, string agentId, string skillName, int proficiency)
    {
        using var response = await client.PostAsync($"{AgentsPath}/{agentId}/skills",
            JsonContent.Create(new { skillName, proficiency }));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<string>> SkillNamesAsync(HttpClient client)
    {
        using var response = await client.GetAsync(SkillsPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
            .Select(n => n!["name"]!.GetValue<string>()).ToList();
    }

    private static async Task<Dictionary<string, int>> AgentSkillsAsync(HttpClient client, string agentId)
    {
        using var response = await client.GetAsync($"{AgentsPath}/{agentId}/skills");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
            .ToDictionary(n => n!["skillName"]!.GetValue<string>(), n => n!["proficiency"]!.GetValue<int>());
    }

    private static async Task<List<string>> AgentsWithSkillAsync(HttpClient client, string skillName)
    {
        using var response = await client.GetAsync($"{SkillsPath}/{skillName}/agents");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
            .Select(n => n!["agentId"]!.GetValue<string>()).ToList();
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql, string agentId)
    {
        await using var db = NpgsqlDataSource.Create(connectionString);
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = Tenant });
        cmd.Parameters.Add(new NpgsqlParameter("AgentId", NpgsqlDbType.Text) { Value = agentId });
        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>One Api replica over <paramref name="connectionString"/>, with the composed PostgreSQL skill catalog.</summary>
    private sealed class Host : AuthenticatedPlatformApiFactory
    {
        private readonly string _connectionString;

        public Host(string connectionString)
        {
            _connectionString = connectionString;
            KeepsComposedSkillCatalog = true;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
        }
    }

    /// <summary>
    /// The PostgreSQL the class runs on: <c>postgres:18-alpine</c> pinned by the digest the store tests use
    /// (<c>Verbara.Platform.Storage.Postgres.Tests</c> <c>MigrationsFixture</c>). Each test creates a database of its own.
    /// </summary>
    public sealed class PostgresContainer : IAsyncLifetime
    {
        private const string Image =
            "postgres:18-alpine@sha256:0115267ab5a509022df9319c77be365cef335adb71556c75f60f5360ee247e71";

        private IContainer? _container;

        private string ConnectionStringFor(string database) =>
            $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
            $"Database={database};Username=postgres;Password=postgres";

        /// <summary>Creates an empty database, applies the Platform migrations and returns its connection string.</summary>
        public async Task<string> CreateDatabaseAsync()
        {
            var name = $"skl_{Guid.NewGuid():N}";
            await using (var admin = NpgsqlDataSource.Create(ConnectionStringFor("postgres")))
            await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
                await cmd.ExecuteNonQueryAsync();
            var connectionString = ConnectionStringFor(name);
            DatabaseMigrationService.ApplyMigrations(connectionString);
            return connectionString;
        }

        public async Task InitializeAsync()
        {
            _container = new ContainerBuilder(Image)
                .WithEnvironment("POSTGRES_PASSWORD", "postgres")
                .WithEnvironment("POSTGRES_DB", "postgres")
                .WithPortBinding(5432, true)
                // `-h 127.0.0.1` probes over TCP: the entrypoint first runs a socket-only temporary server.
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
                .Build();
            await _container.StartAsync();
        }

        public async Task DisposeAsync()
        {
            if (_container is not null) await _container.DisposeAsync();
        }
    }
}
