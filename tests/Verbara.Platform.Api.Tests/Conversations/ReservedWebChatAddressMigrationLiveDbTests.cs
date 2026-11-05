using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// webchat-visitor-identity-two-levels (design D14, spec <c>webchat-visitor-identity</c>): migration <c>028</c> on a real
/// Postgres. Each test gets a fresh database holding every earlier migration, recorded in <c>_migrations</c>, and contacts
/// inserted the way the older contact routes wrote them. The real host then starts over that database, which is what
/// runs the migration before the Api serves. Its data is raw SQL so a test can state each on-disk channel form.
/// </summary>
public sealed class ReservedWebChatAddressMigrationLiveDbTests
    : IClassFixture<ConversationClockReconcileLiveDbTests.PostgresContainer>
{
    private const string Migration = "028_QuarantineReservedWebChatAddresses.sql";
    private const string ResourcePrefix = "Verbara.Platform.Storage.Postgres.Migrations.";

    private const string SessionAddress = "webchat-6a7b8c9d0e1f42a3b4c5d6e7f8a9b0c1";
    private const string VisitorKey = "webchat-visitor:rcfqwP4uUDSA6hgRqgUIE3hCFFsXvDuxhE3MgUu0Y1M";
    private const string OtherTenant = "t-other";

    // The read-only query of the upgrade note. One line, so the note carries these exact bytes.
    private const string AffectedContactsSql =
        "SELECT c.tenant_id, c.contact_id, a->>'address' AS address FROM contacts c CROSS JOIN LATERAL jsonb_array_elements(c.addresses) AS a " +
        "WHERE a->>'channel' IN ('WebChat', '3') AND (starts_with(a->>'address', 'webchat-visitor:') OR starts_with(a->>'address', 'webchat-external:'));";

    private static readonly JsonObject SessionFixture = LoadFixture("webchat-session-create.v1.json");
    private static readonly JsonObject TokenFixture = LoadFixture("webchat-identity-token.v1.json");
    private static readonly JsonObject FixtureRequest = SessionFixture["request"]!["body"]!.AsObject();
    private static readonly string TenantValue = FixtureRequest["tenantId"]!.GetValue<string>();
    private static readonly TenantId Tenant = new(TenantValue);
    private static readonly string CurrentSecret = TokenFixture["testVector"]!["currentSecret"]!.GetValue<string>();
    private static readonly DateTimeOffset FixtureNow =
        DateTimeOffset.FromUnixTimeSeconds(TokenFixture["testVector"]!["now"]!.GetValue<long>());
    private static readonly string ExternalKey =
        TokenFixture["testVector"]!["accepted"]!.AsArray()[0]!["contactAddress"]!.GetValue<string>();

    private readonly ConversationClockReconcileLiveDbTests.PostgresContainer _postgres;

    public ReservedWebChatAddressMigrationLiveDbTests(ConversationClockReconcileLiveDbTests.PostgresContainer postgres) => _postgres = postgres;

    private static JsonObject LoadFixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", name)))!.AsObject();

    private static string Address(object channel, string address) =>
        channel is string name
            ? $$"""{"channel": "{{name}}", "address": "{{address}}"}"""
            : $$"""{"channel": {{channel}}, "address": "{{address}}"}""";

    // ─── What the migration renames, and what it leaves ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deploy_ShouldRenameTheWebChatReservedFormsInPlace_WhenTheApiStarts()
    {
        await using var db = await Database.BeforeTheMigrationAsync(_postgres);
        // The channel written as the name, among other addresses, so the position is measured.
        await db.InsertContactAsync(TenantValue, "c-external",
            $"[{Address("WhatsApp", "5215512345678")}, {Address("WebChat", ExternalKey)}, {Address("WebChat", SessionAddress)}]");
        // The channel written as the ordinal, the form of rows older than the string enum converter.
        await db.InsertContactAsync(TenantValue, "c-visitor", $"[{Address((int)ChannelType.WebChat, VisitorKey)}]");
        await db.InsertContactAsync(OtherTenant, "c-other-tenant", $"[{Address("WebChat", ExternalKey)}]");
        var before = await db.OtherMembersAsync();

        await using var host = await db.StartHostAsync();

        JsonNode.DeepEquals(
                JsonNode.Parse(await db.AddressesAsync(TenantValue, "c-external")),
                JsonNode.Parse($"[{Address("WhatsApp", "5215512345678")}, {Address("WebChat", "legacy-" + ExternalKey)}, {Address("WebChat", SessionAddress)}]"))
            .Should().BeTrue("the renamed address keeps its position and its channel, and it read {0}", await db.AddressesAsync(TenantValue, "c-external"));
        JsonNode.DeepEquals(
                JsonNode.Parse(await db.AddressesAsync(TenantValue, "c-visitor")),
                JsonNode.Parse($"[{Address((int)ChannelType.WebChat, "legacy-" + VisitorKey)}]"))
            .Should().BeTrue("the channel stays in its ordinal form, and it read {0}", await db.AddressesAsync(TenantValue, "c-visitor"));
        JsonNode.DeepEquals(
                JsonNode.Parse(await db.AddressesAsync(OtherTenant, "c-other-tenant")),
                JsonNode.Parse($"[{Address("WebChat", "legacy-" + ExternalKey)}]"))
            .Should().BeTrue("the migration covers every tenant");
        (await db.OtherMembersAsync()).Should().BeEquivalentTo(before, "no other member of a contact changes, its updated_at included");

        (await host.ContactJsonAsync("c-external"))["identityLevel"]!.GetValue<string>().Should().Be(nameof(ContactIdentityLevel.Anonymous));
        (await host.ContactJsonAsync("c-visitor"))["identityLevel"]!.GetValue<string>().Should().Be(nameof(ContactIdentityLevel.Anonymous));
        (await host.ContactJsonAsync("c-external"))["addresses"]!.AsArray()[1]!["address"]!.GetValue<string>().Should().Be("legacy-" + ExternalKey);
        (await host.FindByAddressAsync(ExternalKey)).Should().BeNull("no contact holds the key any more");
        (await host.FindByAddressAsync(VisitorKey)).Should().BeNull("no contact holds the key any more");
    }

    [Fact]
    public async Task Deploy_ShouldLeaveEveryOtherAddressUntouched_WhenTheApiStarts()
    {
        await using var db = await Database.BeforeTheMigrationAsync(_postgres);
        var untouched = new Dictionary<string, string>
        {
            ["c-mixed-case"] = $"[{Address("WebChat", "WebChat-External:x")}]",
            ["c-whatsapp"] = $"[{Address("WhatsApp", "webchat-external:x")}, {Address((int)ChannelType.WhatsApp, VisitorKey)}]",
            ["c-session"] = $"[{Address("WebChat", SessionAddress)}]",
            ["c-renamed-before"] = $"[{Address("WebChat", "legacy-" + ExternalKey)}]",
            ["c-empty"] = "[]",
            ["c-json-null"] = "null",
        };
        foreach (var (contactId, addresses) in untouched)
            await db.InsertContactAsync(TenantValue, contactId, addresses);

        await using var host = await db.StartHostAsync();

        foreach (var (contactId, addresses) in untouched)
        {
            JsonNode.DeepEquals(JsonNode.Parse(await db.AddressesAsync(TenantValue, contactId)), JsonNode.Parse(addresses))
                .Should().BeTrue("{0} holds nothing to rename", contactId);
        }

        (await host.ContactJsonAsync("c-mixed-case"))["identityLevel"]!.GetValue<string>().Should().Be(nameof(ContactIdentityLevel.Anonymous));
        (await host.ContactJsonAsync("c-whatsapp")).ContainsKey("identityLevel").Should().BeFalse();
    }

    // ─── What the rename gives: spec "An address typed through the contact routes before the deploy" ─────────────────

    [Fact]
    public async Task CreateSession_ShouldResolveANewVerifiedContact_WhenTheTypedContactWasRenamed()
    {
        await using var db = await Database.BeforeTheMigrationAsync(_postgres);
        await db.InsertContactAsync(TenantValue, "c-typed", $"[{Address("WebChat", ExternalKey)}]");

        await using var host = await db.StartHostAsync();
        var session = await host.CreateSessionAsync();

        session["identityLevel"]!.GetValue<string>().Should().Be(nameof(ContactIdentityLevel.Verified));
        session["contactId"]!.GetValue<string>().Should().NotBe("c-typed", "a verified session never lands in the contact somebody typed the key into");
        var verified = await host.FindByAddressAsync(ExternalKey);
        verified!.ContactId.Value.Should().Be(session["contactId"]!.GetValue<string>());
        JsonNode.DeepEquals(
                JsonNode.Parse(await db.AddressesAsync(TenantValue, "c-typed")),
                JsonNode.Parse($"[{Address("WebChat", "legacy-" + ExternalKey)}]"))
            .Should().BeTrue("the session adds nothing to the renamed contact");
    }

    // ─── Once per database ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyMigrations_ShouldChangeNothing_WhenItRunsAgain()
    {
        await using var db = await Database.BeforeTheMigrationAsync(_postgres);
        await db.InsertContactAsync(TenantValue, "c-typed", $"[{Address("WebChat", ExternalKey)}]");

        await using var host = await db.StartHostAsync();
        (await db.RecordedRunsAsync()).Should().Be(1L);
        var session = await host.CreateSessionAsync();
        var before = await db.AllRowsAsync();
        before[session["contactId"]!.GetValue<string>()].Should().Contain(ExternalKey, "the key the new code wrote is what a second run must not touch");

        DatabaseMigrationService.ApplyMigrations(db.ConnectionString);

        (await db.RecordedRunsAsync()).Should().Be(1L);
        (await db.AllRowsAsync()).Should().BeEquivalentTo(before, "the runner skips a recorded migration");
    }

    // The runner's record aside, the file's own text does not prefix an address twice.
    [Fact]
    public async Task Migration_ShouldNotPrefixTwice_WhenItsTextRunsAgain()
    {
        await using var db = await Database.BeforeTheMigrationAsync(_postgres);
        await db.InsertContactAsync(TenantValue, "c-typed", $"[{Address("WebChat", ExternalKey)}]");
        await db.RunMigrationTextAsync();
        var before = await db.AllRowsAsync();
        before["c-typed"].Should().Contain("legacy-" + ExternalKey);

        await db.RunMigrationTextAsync();

        (await db.AllRowsAsync()).Should().BeEquivalentTo(before);
    }

    // ─── The upgrade note's query ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpgradeNote_ShouldListExactlyWhatTheMigrationRenames_WhenRunBeforeTheDeploy()
    {
        await using var db = await Database.BeforeTheMigrationAsync(_postgres);
        await db.InsertContactAsync(TenantValue, "c-external", $"[{Address("WebChat", ExternalKey)}, {Address("WebChat", SessionAddress)}]");
        await db.InsertContactAsync(TenantValue, "c-visitor", $"[{Address((int)ChannelType.WebChat, VisitorKey)}]");
        await db.InsertContactAsync(TenantValue, "c-mixed-case", $"[{Address("WebChat", "WebChat-External:x")}]");
        await db.InsertContactAsync(TenantValue, "c-whatsapp", $"[{Address("WhatsApp", ExternalKey)}]");

        (await db.ListAffectedAsync()).Should().BeEquivalentTo(["c-external|" + ExternalKey, "c-visitor|" + VisitorKey]);
        await db.RunMigrationTextAsync();
        (await db.ListAffectedAsync()).Should().BeEmpty("the query lists what the migration renames, so nothing is left after it");

        var changelog = await File.ReadAllTextAsync(RepositoryFile("CHANGELOG.md"));
        changelog.Should().Contain(AffectedContactsSql, "the upgrade note carries this query");
    }

    private static string RepositoryFile(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Join(directory.FullName, name);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"No {name} above {AppContext.BaseDirectory}.");
    }

    // ─── Harness ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A fresh database at the migration before <see cref="Migration"/>, as a deployed older release left it.</summary>
    private sealed class Database : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _ds;

        private Database(string connectionString)
        {
            ConnectionString = connectionString;
            _ds = NpgsqlDataSource.Create(connectionString);
        }

        public string ConnectionString { get; }

        public static async Task<Database> BeforeTheMigrationAsync(ConversationClockReconcileLiveDbTests.PostgresContainer postgres)
        {
            var name = $"rsv_{Guid.NewGuid():N}";
            await using (var admin = NpgsqlDataSource.Create(postgres.ConnectionStringFor("postgres")))
            await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
                await cmd.ExecuteNonQueryAsync();

            var db = new Database(postgres.ConnectionStringFor(name));
            await db.ExecuteAsync("CREATE TABLE _migrations (name TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())");
            foreach (var (file, sql) in Migrations().Where(m => string.CompareOrdinal(m.File, Migration) < 0))
            {
                await db.ExecuteAsync(sql);
                await db.ExecuteAsync("INSERT INTO _migrations (name) VALUES (@Name)", new NpgsqlParameter("Name", NpgsqlDbType.Text) { Value = file });
            }

            return db;
        }

        public Task RunMigrationTextAsync() => ExecuteAsync(Migrations().Single(m => m.File == Migration).Sql);

        public Task InsertContactAsync(string tenantId, string contactId, string addressesJson) =>
            ExecuteAsync(
                "INSERT INTO contacts (contact_id, tenant_id, first_name, addresses, custom_fields, channel_consent, created_at, updated_at, updated_by) " +
                "VALUES (@ContactId, @TenantId, 'Ana', @Addresses::jsonb, '{\"campo\": \"valor\"}'::jsonb, '{\"3\": true}'::jsonb, @CreatedAt, @UpdatedAt, 'agente')",
                new NpgsqlParameter("ContactId", NpgsqlDbType.Text) { Value = contactId },
                new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId },
                new NpgsqlParameter("Addresses", NpgsqlDbType.Text) { Value = addressesJson },
                new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = FixtureNow.AddDays(-30) },
                new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = FixtureNow.AddDays(-20) });

        public async Task<string> AddressesAsync(string tenantId, string contactId)
        {
            await using var cmd = _ds.CreateCommand("SELECT addresses::text FROM contacts WHERE tenant_id = @TenantId AND contact_id = @ContactId");
            cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId });
            cmd.Parameters.Add(new NpgsqlParameter("ContactId", NpgsqlDbType.Text) { Value = contactId });
            return (string)(await cmd.ExecuteScalarAsync())!;
        }

        /// <summary>Every member of every contact but its addresses, as text.</summary>
        public Task<Dictionary<string, string>> OtherMembersAsync() => RowsAsync("(to_jsonb(c) - 'addresses')::text");

        /// <summary>Every member of every contact, as text.</summary>
        public Task<Dictionary<string, string>> AllRowsAsync() => RowsAsync("to_jsonb(c)::text");

        private async Task<Dictionary<string, string>> RowsAsync(string expression)
        {
            var rows = new Dictionary<string, string>();
            await using var cmd = _ds.CreateCommand($"SELECT contact_id, {expression} FROM contacts c");
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows[reader.GetString(0)] = reader.GetString(1);
            return rows;
        }

        public async Task<long> RecordedRunsAsync()
        {
            await using var cmd = _ds.CreateCommand("SELECT count(*) FROM _migrations WHERE name = @Name");
            cmd.Parameters.Add(new NpgsqlParameter("Name", NpgsqlDbType.Text) { Value = Migration });
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        public async Task<List<string>> ListAffectedAsync()
        {
            var rows = new List<string>();
            await using var cmd = _ds.CreateCommand(AffectedContactsSql);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add($"{reader.GetString(1)}|{reader.GetString(2)}");
            return rows;
        }

        public async Task<ApiHost> StartHostAsync()
        {
            var host = new ApiHost(ConnectionString) { ApiKeyTenantId = TenantValue };
            await host.StartAsync();
            return host;
        }

        private async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
        {
            await using var cmd = _ds.CreateCommand(sql);
            cmd.Parameters.AddRange(parameters);
            await cmd.ExecuteNonQueryAsync();
        }

        private static IEnumerable<(string File, string Sql)> Migrations()
        {
            var assembly = typeof(Storage.Postgres.ServiceCollectionExtensions).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames()
                         .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                yield return (resource[ResourcePrefix.Length..], reader.ReadToEnd());
            }
        }

        public async ValueTask DisposeAsync() => await _ds.DisposeAsync();
    }

    /// <summary>The real host at the token fixture's <c>now</c>, with the fixture's secret stored for its tenant.</summary>
    private sealed class ApiHost(string connectionString) : AuthenticatedPlatformApiFactory
    {
        private HttpClient _client = null!;

        public Task StartAsync()
        {
            _client = CreateClient();
            _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestApiKey}");
            _client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantValue);
            return Task.CompletedTask;
        }

        public Task<Contact?> FindByAddressAsync(string address) =>
            Services.GetRequiredService<IContactStore>().FindByAddressAsync(
                Tenant, new ChannelAddress(ChannelType.WebChat, address), CancellationToken.None);

        public async Task<JsonObject> ContactJsonAsync(string contactId) =>
            JsonNode.Parse(await _client.GetStringAsync(new Uri($"/api/v1/contacts/{contactId}", UriKind.Relative)))!.AsObject();

        public async Task<JsonObject> CreateSessionAsync()
        {
            using var content = new StringContent(FixtureRequest.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
            using var response = await _client.PostAsync(new Uri("/api/v1/webchat/sessions", UriKind.Relative), content);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            return JsonNode.Parse(body)!.AsObject();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureServices((_, services) =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new WebChatSessionIdentityLevelTests.MutableClockForSessions(FixtureNow));
            });
            var host = base.CreateHost(builder);
            SeedTestCustomerTenant(host.Services, TenantValue);
            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(
                new TenantChannelConfig
                {
                    TenantId = Tenant,
                    Channel = ChannelType.WebChat,
                    Credentials = new Dictionary<string, string> { [ChannelCredentialSchemas.IdentityTokenSecretKey] = CurrentSecret },
                },
                CancellationToken.None).GetAwaiter().GetResult();
            return host;
        }
    }
}
