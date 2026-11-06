using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Conversations;

namespace Verbara.Platform.Api.Tests.Routing;

/// <summary>
/// conversations-return-to-last-agent task 1.3 (design D2, spec "The queue override MUST be set by SQL only"): through the
/// real host over a real Postgres, the queue routes never write <c>queue_configs.last_agent_routing_enabled</c>. A create
/// leaves it <c>NULL</c>; an update of another field keeps what SQL set. (The update reloads the queue first, so a
/// write-back of the entity's own value would pass this case; the store's stale-entity test is the one that bites.)
/// </summary>
public sealed class QueueLastAgentOverrideLiveDbTests : IClassFixture<ConversationClockReconcileLiveDbTests.PostgresContainer>
{
    private readonly ConversationClockReconcileLiveDbTests.PostgresContainer _postgres;

    public QueueLastAgentOverrideLiveDbTests(ConversationClockReconcileLiveDbTests.PostgresContainer postgres) => _postgres = postgres;

    [Fact]
    public async Task UpdateQueue_ShouldKeepTheSqlOverrideAndLandTheName_WhenAnAdministratorRenamesTheQueue()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var queueId = await env.CreateQueueAsync("Support");
        await env.SetOverrideAsync(queueId, false);

        using var response = await env.Client.PutAsync(
            new Uri($"/api/v1/admin/queues/{queueId}", UriKind.Relative), JsonContent.Create(new { name = "Support (renamed)" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await env.ScalarAsync("SELECT name FROM queue_configs WHERE queue_id = @Id", queueId)).Should().Be("Support (renamed)");
        (await env.ScalarAsync("SELECT last_agent_routing_enabled FROM queue_configs WHERE queue_id = @Id", queueId))
            .Should().Be(false, "a queue save must not change the SQL-only override");
    }

    [Fact]
    public async Task CreateQueue_ShouldLeaveTheOverrideNull_WhenAnAdministratorCreatesAQueue()
    {
        await using var env = await Env.CreateAsync(_postgres);

        var queueId = await env.CreateQueueAsync("Sales");

        (await env.ScalarAsync("SELECT last_agent_routing_enabled FROM queue_configs WHERE queue_id = @Id", queueId))
            .Should().Be(DBNull.Value, "a new queue follows the host");
    }

    private sealed class Host(string connectionString) : AuthenticatedPlatformApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        }
    }

    private sealed class Env : IAsyncDisposable
    {
        private readonly Host _host;
        private readonly NpgsqlDataSource _db;

        private Env(string connectionString)
        {
            _host = new Host(connectionString);
            Client = _host.CreateAuthenticatedClient();
            _db = NpgsqlDataSource.Create(connectionString);
        }

        public HttpClient Client { get; }

        public static async Task<Env> CreateAsync(ConversationClockReconcileLiveDbTests.PostgresContainer postgres)
        {
            var name = $"qla_{Guid.NewGuid():N}";
            await using (var admin = NpgsqlDataSource.Create(postgres.ConnectionStringFor("postgres")))
            await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
                await cmd.ExecuteNonQueryAsync();
            var connectionString = postgres.ConnectionStringFor(name);
            DatabaseMigrationService.ApplyMigrations(connectionString);
            return new Env(connectionString);
        }

        public async Task<string> CreateQueueAsync(string name)
        {
            using var response = await Client.PostAsync(
                new Uri("/api/v1/admin/queues", UriKind.Relative), JsonContent.Create(new { name }));
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Created, body);
            return JsonNode.Parse(body)!["id"]!.GetValue<string>();
        }

        public async Task SetOverrideAsync(string queueId, bool value)
        {
            await using var cmd = _db.CreateCommand("UPDATE queue_configs SET last_agent_routing_enabled = @Value WHERE queue_id = @Id");
            cmd.Parameters.Add(new NpgsqlParameter("Value", NpgsqlDbType.Boolean) { Value = value });
            cmd.Parameters.Add(new NpgsqlParameter("Id", NpgsqlDbType.Text) { Value = queueId });
            (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        }

        public async Task<object?> ScalarAsync(string sql, string queueId)
        {
            await using var cmd = _db.CreateCommand(sql);
            cmd.Parameters.Add(new NpgsqlParameter("Id", NpgsqlDbType.Text) { Value = queueId });
            return await cmd.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.DisposeAsync();
            await _db.DisposeAsync();
        }
    }
}
