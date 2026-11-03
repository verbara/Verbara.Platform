using System.Text.Json;
using Npgsql;
using Verbara.Platform.Conversations;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Storage.TestSupport;
using Xunit.Abstractions;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// <see cref="ConversationInactivityStoreContract"/> against <see cref="PostgresConversationStore"/> on a real Postgres
/// (a fresh database per test, every migration applied), plus the plan of the sweep's list statement (task 1.5).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresConversationInactivityStoreTests : ConversationInactivityStoreContract, IClassFixture<ConversationClockFixture>
{
    private readonly ConversationClockFixture _fixture;
    private readonly ITestOutputHelper _output;

    public PostgresConversationInactivityStoreTests(ConversationClockFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    protected override async Task<IConversationStore> NewStoreAsync() =>
        new PostgresConversationStore(await _fixture.CreateDatabaseAsync());

    // D2/D4: the range is sargable on idx_conversations_state_last_message, so the in-window rows are never visited.
    // Seed: 50 000 closed rows, 5 000 in-scope open rows inside the window (clock 1-71 h, the five states) and 200
    // idle past it; ANALYZE; then EXPLAIN (ANALYZE, FORMAT JSON) of the very statement ListInactiveAsync runs.
    [Fact]
    public async Task ListInactiveAsync_ShouldRangeScanTheClockIndexWithoutVisitingInWindowRows_WhenTheTenantIsLarge()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        await ConversationClockFixture.ExecuteAsync(ds,
            "INSERT INTO conversations (conversation_id, tenant_id, contact_id, channel, state, metadata, created_at, closed_at, last_message_at) " +
            "SELECT 'closed-' || g, @T, 'contact-' || g, 1, 50, '{}'::jsonb, @Now - interval '400 hours', @Now - interval '300 hours', " +
            "       @Now - interval '350 hours' FROM generate_series(1, 50000) g",
            TenantParameter(), NowParameter());
        await ConversationClockFixture.ExecuteAsync(ds,
            "INSERT INTO conversations (conversation_id, tenant_id, contact_id, channel, state, metadata, created_at, last_message_at) " +
            "SELECT 'open-' || g, @T, 'contact-o' || g, 1, (ARRAY[10, 11, 12, 30, 40])[1 + g % 5], '{}'::jsonb, " +
            "       @Now - interval '100 hours', @Now - make_interval(hours => 1 + g % 71) FROM generate_series(1, 5000) g",
            TenantParameter(), NowParameter());
        await ConversationClockFixture.ExecuteAsync(ds,
            "INSERT INTO conversations (conversation_id, tenant_id, contact_id, channel, state, metadata, created_at, last_message_at) " +
            "SELECT 'idle-' || g, @T, 'contact-i' || g, 1, (ARRAY[10, 11, 12, 30, 40])[1 + g % 5], '{}'::jsonb, " +
            "       @Now - interval '300 hours', @Now - make_interval(hours => 73 + g % 120) FROM generate_series(1, 200) g",
            TenantParameter(), NowParameter());
        await ConversationClockFixture.ExecuteAsync(ds, "ANALYZE conversations");

        string planJson;
        await using (var cmd = ds.CreateCommand("EXPLAIN (ANALYZE, FORMAT JSON) " + PostgresConversationStore.ListInactiveSql))
        {
            PostgresConversationStore.BindListInactive(
                cmd.Parameters, Tenant, ConversationStateMachine.InactivityCloseStates, Now.AddHours(-72), 100);
            planJson = (string)(await cmd.ExecuteScalarAsync())!;
        }
        _output.WriteLine(planJson);

        var nodes = new List<JsonElement>();
        using var doc = JsonDocument.Parse(planJson);
        Collect(doc.RootElement[0].GetProperty("Plan"), nodes);

        nodes.Select(n => n.GetProperty("Node Type").GetString()).Should().NotContain("Seq Scan", "no sequential scan");
        var scan = nodes.Where(n => n.TryGetProperty("Index Name", out var name)
                && name.GetString() == "idx_conversations_state_last_message").ToList();
        scan.Should().ContainSingle("the scan node uses the clock index");
        var indexCond = scan[0].GetProperty("Index Cond").GetString();
        indexCond.Should().Contain("state").And.Contain("last_message_at <",
            "both the state condition and the clock range are in the Index Cond");
        var removedByFilter = nodes
            .Select(n => n.TryGetProperty("Rows Removed by Filter", out var removed) ? removed.GetDouble() : 0)
            .ToList();
        removedByFilter.Should().OnlyContain(removed => removed < 1000, "the in-window rows are never visited");
        scan[0].GetProperty("Actual Rows").GetDouble().Should().BeLessThanOrEqualTo(200);
    }

    private static NpgsqlParameter TenantParameter() => new("T", NpgsqlTypes.NpgsqlDbType.Text) { Value = Tenant.Value };

    private static NpgsqlParameter NowParameter() => new("Now", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = Now };

    private static void Collect(JsonElement plan, List<JsonElement> nodes)
    {
        nodes.Add(plan.Clone());
        if (plan.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                Collect(child, nodes);
    }
}
