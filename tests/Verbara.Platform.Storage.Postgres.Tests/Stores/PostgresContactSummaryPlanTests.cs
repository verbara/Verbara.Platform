using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Xunit.Abstractions;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// contact-history-opens-by-assignment task 3.3, cell T-4 of the frozen measurement plan (task 0.3): the plan of the
/// contact summary page statement (<see cref="PostgresConversationStore.ContactSummariesPageSql"/>, design D6) on a
/// contact with 100 conversations of 1,000 messages each, and a page of 100. The lateral read of the newest message
/// must use <c>idx_messages_conversation</c> and never scan <c>messages</c> sequentially; the control, with
/// <c>enable_indexscan = off</c>, must show that sequential scan. The warm execution times are written to the output
/// for the measurement card, and are not asserted (a timing assertion would only make CI flaky).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresContactSummaryPlanTests : IClassFixture<ConversationClockFixture>
{
    private static readonly TenantId Tenant = new("t-summary-plan");
    private const string Contact = "contact-summary-plan";
    private const int Conversations = 100;
    private const int MessagesPerConversation = 1000;
    private const int PageSize = 100;
    private const int WarmRuns = 3;
    private const int MeasuredRuns = 5;

    private readonly ConversationClockFixture _fixture;
    private readonly ITestOutputHelper _output;

    public PostgresContactSummaryPlanTests(ConversationClockFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task ContactSummariesPage_ShouldReadTheNewestMessageThroughItsIndex_WhenTheContactHas100ConversationsOf1000Messages()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        await ConversationClockFixture.ExecuteAsync(ds,
            "INSERT INTO conversations (conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, metadata, created_at, closed_at, last_message_at) " +
            "SELECT 'conv-' || lpad(g::text, 3, '0'), @T, @C, 1, 50, 2, 'agent-' || (g % 7), '{}'::jsonb, " +
            "       now() - make_interval(days => g), now() - make_interval(days => g) + interval '1 hour', " +
            "       now() - make_interval(days => g) + interval '50 minutes' FROM generate_series(1, " + Conversations + ") g",
            Parameter("T", Tenant.Value), Parameter("C", Contact));
        await ConversationClockFixture.ExecuteAsync(ds,
            "INSERT INTO messages (message_id, conversation_id, tenant_id, direction, channel, sender_id, content, delivery_status, created_at) " +
            "SELECT 'm-' || c || '-' || m, 'conv-' || lpad(c::text, 3, '0'), @T, m % 2, 1, 'sender', " +
            "       '{\"blocks\":[{\"type\":\"text\",\"text\":\"message body\"}]}'::jsonb, 1, " +
            "       now() - make_interval(days => c) + make_interval(secs => m) " +
            "FROM generate_series(1, " + Conversations + ") c, generate_series(1, " + MessagesPerConversation + ") m",
            Parameter("T", Tenant.Value));
        await ConversationClockFixture.ExecuteAsync(ds, "ANALYZE");

        await using var connection = await ds.OpenConnectionAsync();
        for (var i = 0; i < WarmRuns; i++)
            await ExplainAsync(connection);

        var times = new List<double>();
        JsonElement plan = default;
        for (var i = 0; i < MeasuredRuns; i++)
        {
            plan = await ExplainAsync(connection);
            times.Add(plan.GetProperty("Execution Time").GetDouble());
        }

        _output.WriteLine("T-4 plan: " + Describe(plan.GetProperty("Plan")));
        times.Sort();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"T-4 warm execution ms: [{string.Join(", ", times)}], median {times[MeasuredRuns / 2]}"));

        var nodes = Nodes(plan.GetProperty("Plan"));
        nodes.Where(n => Is(n, "Relation Name", "messages")).Select(n => n.GetProperty("Node Type").GetString())
            .Should().NotContain("Seq Scan", "the flip rule: no sequential scan on messages");
        nodes.Should().Contain(n => Is(n, "Index Name", "idx_messages_conversation"),
            "the newest message is read through idx_messages_conversation");
        _output.WriteLine("T-4 conversations access: " + string.Join("; ", nodes
            .Where(n => Is(n, "Relation Name", "conversations"))
            .Select(n => $"{n.GetProperty("Node Type").GetString()} {(n.TryGetProperty("Index Name", out var ix) ? ix.GetString() : "-")}")));
        plan.GetProperty("Plan").GetProperty("Actual Rows").GetDouble().Should().Be(PageSize, "a page of 100");

        // The frozen control: enable_indexscan = off. Measured 2026-11-04, the planner then reads messages through a
        // bitmap scan of the same index, not a sequential scan, so the frozen control does not show its predicted
        // signal; the card records that. Asserted here: the access path to messages changes away from the index scan.
        await SetAsync(connection, "SET enable_indexscan = off");
        var control = await ExplainAsync(connection);
        _output.WriteLine("T-4 control plan (enable_indexscan = off): " + Describe(control.GetProperty("Plan")));
        Nodes(control.GetProperty("Plan"))
            .Should().NotContain(n => Is(n, "Node Type", "Index Scan") && Is(n, "Index Name", "idx_messages_conversation"),
                "with index scans off the newest message is not read through an index scan");

        // Outside the frozen plan, added after the frozen control fell back to a bitmap scan: with bitmap scans off as
        // well, the same statement shows the sequential scan on messages, so the check above can see one.
        await SetAsync(connection, "SET enable_bitmapscan = off");
        var noIndex = await ExplainAsync(connection);
        _output.WriteLine("T-4 control plan (enable_indexscan and enable_bitmapscan off): " + Describe(noIndex.GetProperty("Plan")));
        Nodes(noIndex.GetProperty("Plan"))
            .Should().Contain(n => Is(n, "Relation Name", "messages") && Is(n, "Node Type", "Seq Scan"),
                "with no index path left, the detector must see the sequential scan on messages");
        await SetAsync(connection, "RESET ALL");

        // Outside the frozen plan: the same contact among 10,000 conversations of other contacts, to see the conversations
        // access path once the table is larger than the contact (written to the output, not asserted).
        await ConversationClockFixture.ExecuteAsync(ds,
            "INSERT INTO conversations (conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, metadata, created_at) " +
            "SELECT 'other-' || g, @T, 'contact-other-' || g, 1, 50, 2, 'agent-x', '{}'::jsonb, now() - make_interval(hours => g) " +
            "FROM generate_series(1, 10000) g",
            Parameter("T", Tenant.Value));
        await ConversationClockFixture.ExecuteAsync(ds, "ANALYZE conversations");
        for (var i = 0; i < WarmRuns; i++)
            await ExplainAsync(connection);
        var larger = await ExplainAsync(connection);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"T-4 with 10,000 other conversations: {larger.GetProperty("Execution Time").GetDouble()} ms; ")
            + Describe(larger.GetProperty("Plan")));
        Nodes(larger.GetProperty("Plan")).Where(n => Is(n, "Relation Name", "messages"))
            .Select(n => n.GetProperty("Node Type").GetString())
            .Should().NotContain("Seq Scan", "no sequential scan on messages in the larger table either");
    }

    private static async Task SetAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<JsonElement> ExplainAsync(NpgsqlConnection connection)
    {
        await using var cmd = new NpgsqlCommand(
            "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + PostgresConversationStore.ContactSummariesPageSql(ownedByAgent: false),
            connection);
        PostgresConversationStore.BindContactSummariesPage(cmd.Parameters, Tenant, EntityId.From(Contact), null, 1, PageSize);
        using var doc = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync())!);
        return doc.RootElement[0].Clone();
    }

    private static NpgsqlParameter Parameter(string name, string value) => new(name, NpgsqlDbType.Text) { Value = value };

    private static bool Is(JsonElement node, string property, string value) =>
        node.TryGetProperty(property, out var actual) && actual.GetString() == value;

    private static string Describe(JsonElement plan) =>
        string.Join(" | ", Nodes(plan).Select(n => string.Create(CultureInfo.InvariantCulture,
            $"{n.GetProperty("Node Type").GetString()}" +
            $"{(n.TryGetProperty("Relation Name", out var relation) ? " on " + relation.GetString() : "")}" +
            $"{(n.TryGetProperty("Index Name", out var index) ? " using " + index.GetString() : "")}" +
            $" rows={n.GetProperty("Actual Rows").GetDouble()} loops={n.GetProperty("Actual Loops").GetDouble()}")));

    private static List<JsonElement> Nodes(JsonElement plan)
    {
        var nodes = new List<JsonElement> { plan };
        if (plan.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                nodes.AddRange(Nodes(child));
        return nodes;
    }
}
