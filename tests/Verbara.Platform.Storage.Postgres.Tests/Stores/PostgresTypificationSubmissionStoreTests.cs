using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using static Verbara.Platform.Storage.Postgres.Tests.Stores.DialerDispatchTestData;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// dialer-honours-dnc-callbacks-and-agent-dispositions (design D15, D16, D17): <c>TryInsertAsync</c> on the REAL
/// migrations (incl. <c>025_DialerDispositionDispatches.sql</c>) — the first submission of a Conversation wins, decided
/// by the insert, and its dispatch row commits with it or not at all.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresTypificationSubmissionStoreTests : IClassFixture<MigrationsFixture>
{
    private readonly MigrationsFixture _fixture;
    private readonly PostgresTypificationSubmissionStore _store;
    private readonly PostgresDialerDispositionDispatchStore _dispatches;

    public PostgresTypificationSubmissionStoreTests(MigrationsFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresTypificationSubmissionStore(fixture.DataSource);
        _dispatches = new PostgresDialerDispositionDispatchStore(fixture.DataSource);
    }

    private static string NewConversation() => $"conv-{Guid.NewGuid():N}";

    [Fact]
    public async Task TryInsertAsync_ShouldStoreTheSubmissionAndItsDispatchTogether_WhenNoneExists()
    {
        var conversation = NewConversation();

        var stored = await _store.TryInsertAsync(Submission(conversation, "leaf-a"), Dispatch(conversation), CancellationToken.None);

        stored.Should().BeTrue();
        (await _store.GetByConversationIdAsync(new TenantId(Tenant), EntityId.From(conversation), CancellationToken.None))!
            .LeafNodeId.Value.Should().Be("leaf-a");
        var row = await _dispatches.GetAsync(Tenant, conversation, CancellationToken.None);
        row.Should().NotBeNull();
        row!.ClaimId.Should().Be("11111111111111111111111111111111");
        row.ClaimExpiresAt.Should().Be(T0.AddMinutes(1));
        row.Attempts.Should().Be(1);
        row.Result.Should().BeNull();
        row.RequestsCallback.Should().BeTrue();
        row.DialerContactId.Should().Be(4711);
    }

    [Fact]
    public async Task TryInsertAsync_ShouldStoreNothingAndReportFalse_WhenASubmissionExists()
    {
        var conversation = NewConversation();
        await _store.SaveAsync(Submission(conversation, "leaf-first"), CancellationToken.None);

        var stored = await _store.TryInsertAsync(Submission(conversation, "leaf-second"), Dispatch(conversation), CancellationToken.None);

        stored.Should().BeFalse();
        (await _store.GetByConversationIdAsync(new TenantId(Tenant), EntityId.From(conversation), CancellationToken.None))!
            .LeafNodeId.Value.Should().Be("leaf-first");
        (await _dispatches.GetAsync(Tenant, conversation, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TryInsertAsync_ShouldStoreExactlyOne_WhenTwoFirstSubmissionsRunConcurrently()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var conversation = NewConversation();
            using var start = new ManualResetEventSlim();
            var a = Task.Run(async () =>
            {
                start.Wait();
                return await _store.TryInsertAsync(Submission(conversation, "leaf-a"), Dispatch(conversation, codeId: 1), CancellationToken.None);
            });
            var b = Task.Run(async () =>
            {
                start.Wait();
                return await _store.TryInsertAsync(Submission(conversation, "leaf-b"), Dispatch(conversation, codeId: 2), CancellationToken.None);
            });
            start.Set();
            var results = await Task.WhenAll(a, b);

            results.Count(r => r).Should().Be(1, "exactly one first submission is stored (iteration {0})", iteration);
            var winnerLeaf = results[0] ? "leaf-a" : "leaf-b";
            (await _store.GetByConversationIdAsync(new TenantId(Tenant), EntityId.From(conversation), CancellationToken.None))!
                .LeafNodeId.Value.Should().Be(winnerLeaf);
            (await CountDispatchRowsAsync(conversation)).Should().Be(1);
            (await _dispatches.GetAsync(Tenant, conversation, CancellationToken.None))!
                .DispositionCodeId.Should().Be(results[0] ? 1 : 2, "the dispatch is the winner's");
        }
    }

    [Fact]
    public async Task TryInsertAsync_ShouldStoreNeither_WhenTheDispatchRowIsRefused()
    {
        var conversation = NewConversation();

        var act = () => _store.TryInsertAsync(Submission(conversation, "leaf-a"), Dispatch(conversation, actionId: null), CancellationToken.None);

        await act.Should().ThrowAsync<PostgresException>().Where(e => e.SqlState == PostgresErrorCodes.NotNullViolation);
        (await _store.GetByConversationIdAsync(new TenantId(Tenant), EntityId.From(conversation), CancellationToken.None))
            .Should().BeNull("the submission rolls back with the refused dispatch");
        (await CountDispatchRowsAsync(conversation)).Should().Be(0);
    }

    [Fact]
    public async Task TryInsertAsync_ShouldStoreTheSubmissionAlone_WhenNoDispatchIsOwed()
    {
        var conversation = NewConversation();

        (await _store.TryInsertAsync(Submission(conversation, "leaf-a"), null, CancellationToken.None)).Should().BeTrue();

        (await CountDispatchRowsAsync(conversation)).Should().Be(0);
        (await _store.TryInsertAsync(Submission(conversation, "leaf-b"), null, CancellationToken.None)).Should().BeFalse();
    }

    private async Task<long> CountDispatchRowsAsync(string conversation)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM dialer_disposition_dispatches WHERE tenant_id = @t AND conversation_id = @c", conn);
        cmd.Parameters.AddWithValue("t", Tenant);
        cmd.Parameters.AddWithValue("c", conversation);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
