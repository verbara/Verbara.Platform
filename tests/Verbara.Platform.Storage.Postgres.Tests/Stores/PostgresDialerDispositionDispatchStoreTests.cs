using Verbara.Platform.Storage.Postgres.Stores;
using static Verbara.Platform.Storage.Postgres.Tests.Stores.DialerDispatchTestData;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// dialer-honours-dnc-callbacks-and-agent-dispositions (design D16): the dispatch claims of
/// <see cref="PostgresDialerDispositionDispatchStore"/> on the REAL migrations. The due-row claim is global, so each
/// test starts from an empty table; this class owns its <see cref="MigrationsFixture"/> container and xUnit runs its
/// tests one after the other.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresDialerDispositionDispatchStoreTests : IClassFixture<MigrationsFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private readonly MigrationsFixture _fixture;
    private readonly PostgresTypificationSubmissionStore _submissions;
    private readonly PostgresDialerDispositionDispatchStore _store;

    public PostgresDialerDispositionDispatchStoreTests(MigrationsFixture fixture)
    {
        _fixture = fixture;
        _submissions = new PostgresTypificationSubmissionStore(fixture.DataSource);
        _store = new PostgresDialerDispositionDispatchStore(fixture.DataSource);
    }

    public async Task InitializeAsync()
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dialer_disposition_dispatches;";
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> InsertAsync(DateTimeOffset expiresAt, int attempts = 1)
    {
        var conversation = $"conv-{Guid.NewGuid():N}";
        (await _submissions.TryInsertAsync(
            Submission(conversation, "leaf-a"), Dispatch(conversation, expiresAt: expiresAt, attempts: attempts), CancellationToken.None))
            .Should().BeTrue();
        return conversation;
    }

    [Fact]
    public async Task ClaimDueAsync_ShouldReturnTheRowWithAFreshClaimAndOneMoreAttempt_WhenItsClaimExpired()
    {
        var conversation = await InsertAsync(expiresAt: T0.AddMinutes(1));
        var now = T0.AddMinutes(2);

        var claimed = await _store.ClaimDueAsync(50, now, now + Lease, CancellationToken.None);

        var row = claimed.Should().ContainSingle().Subject;
        row.ConversationId.Should().Be(conversation);
        row.ClaimId.Should().NotBe("11111111111111111111111111111111").And.HaveLength(32);
        row.ClaimExpiresAt.Should().Be(now + Lease);
        row.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task ClaimDueAsync_ShouldSkipTheRow_WhenAnotherClaimIsLive()
    {
        await InsertAsync(expiresAt: T0.AddMinutes(1));
        var now = T0.AddSeconds(30);

        var claimed = await _store.ClaimDueAsync(50, now, now + Lease, CancellationToken.None);

        claimed.Should().BeEmpty("the request's claim is live for another 30 seconds");
    }

    [Fact]
    public async Task ClaimDueAsync_ShouldClaimEachRowOnce_WhenTwoClaimersRunConcurrently()
    {
        var conversations = new List<string>();
        for (var i = 0; i < 20; i++)
            conversations.Add(await InsertAsync(expiresAt: T0));
        var now = T0.AddMinutes(1);
        using var start = new ManualResetEventSlim();

        var claimers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return await _store.ClaimDueAsync(50, now, now + Lease, CancellationToken.None);
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(claimers);

        var all = results.SelectMany(r => r).Select(r => r.ConversationId).ToList();
        all.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(conversations, "every due row is claimed exactly once");
    }

    [Fact]
    public async Task CompleteAsync_ShouldChangeNothing_WhenTheClaimWasTakenOver()
    {
        var conversation = await InsertAsync(expiresAt: T0);
        var now = T0.AddMinutes(1);
        var takenOver = (await _store.ClaimDueAsync(50, now, now + Lease, CancellationToken.None)).Single();
        var original = Dispatch(conversation);

        var completed = await _store.CompleteAsync(original, "Applied", true, "11111111111111111111111111111111", CancellationToken.None);

        completed.Should().BeFalse();
        var row = await _store.GetAsync(Tenant, conversation, CancellationToken.None);
        row!.Result.Should().BeNull();
        row.PublishedAt.Should().BeNull();
        row.ClaimId.Should().Be(takenOver.ClaimId);
        (await _store.CompleteAsync(takenOver, "AlreadyApplied", false, takenOver.ClaimId!, CancellationToken.None))
            .Should().BeTrue("the holder of the live claim completes it");
    }

    [Fact]
    public async Task ReleaseAsync_ShouldDeferTheRowAndKeepItsAttempts_WhenTheHandoffFailed()
    {
        var conversation = await InsertAsync(expiresAt: T0.AddMinutes(1));
        var row = Dispatch(conversation);

        var released = await _store.ReleaseAsync(row, "boom", T0.AddSeconds(5), row.ClaimId!, CancellationToken.None);

        released.Should().BeTrue();
        var stored = await _store.GetAsync(Tenant, conversation, CancellationToken.None);
        stored!.Attempts.Should().Be(1);
        stored.NextAttemptAt.Should().Be(T0.AddSeconds(5));
        stored.ClaimId.Should().BeNull();
        stored.ClaimExpiresAt.Should().BeNull();
        stored.LastError.Should().Be("boom");
        stored.Result.Should().BeNull();
        (await _store.ClaimDueAsync(50, T0.AddSeconds(4), T0.AddMinutes(2), CancellationToken.None)).Should().BeEmpty();
        (await _store.ClaimDueAsync(50, T0.AddSeconds(5), T0.AddMinutes(2), CancellationToken.None)).Should().ContainSingle();
        (await _store.CountPendingAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task ReleaseAsync_ShouldChangeNothing_WhenTheClaimWasTakenOver()
    {
        var conversation = await InsertAsync(expiresAt: T0);
        var now = T0.AddMinutes(1);
        var takenOver = (await _store.ClaimDueAsync(50, now, now + Lease, CancellationToken.None)).Single();

        var released = await _store.ReleaseAsync(Dispatch(conversation), "late", T0.AddHours(1), "11111111111111111111111111111111", CancellationToken.None);

        released.Should().BeFalse();
        var stored = await _store.GetAsync(Tenant, conversation, CancellationToken.None);
        stored!.ClaimId.Should().Be(takenOver.ClaimId);
        stored.LastError.Should().BeNull();
    }

    [Fact]
    public async Task CountPendingAsync_ShouldCountOnlyRowsWithoutAResult()
    {
        var first = await InsertAsync(expiresAt: T0.AddMinutes(1));
        await InsertAsync(expiresAt: T0.AddMinutes(1));
        await _store.CompleteAsync(Dispatch(first), "Applied", true, "11111111111111111111111111111111", CancellationToken.None);

        (await _store.CountPendingAsync(CancellationToken.None)).Should().Be(1);
        (await _store.GetAsync(Tenant, first, CancellationToken.None))!.PublishedAt.Should().NotBeNull();
    }
}
