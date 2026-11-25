using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity.Redis.Tests;
using Verbara.Platform.Queues;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Redis.Tests;

/// <summary>
/// agent-assist-works-end-to-end (design D12, Monitor list) on a real Redis: replica A (the engine's) files each session's
/// record through its own connection, replica B reads the list through another, as two Api processes do. The records'
/// clock is one <see cref="FakeTimeProvider"/>, started at the wall clock, so a heartbeat is an advance.
/// </summary>
[Collection("Redis")]
public sealed class AgentAssistLiveSessionStoreRedisTests : IAsyncLifetime
{
    private const string LinkedId = "1712345678.60";

    private readonly RedisFixture _redis;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IAgentStore _agents = NoAgents();

    private static IAgentStore NoAgents()
    {
        var agents = Substitute.For<IAgentStore>();
        agents.GetByIdsAsync(Arg.Any<TenantId>(), Arg.Any<IReadOnlyCollection<EntityId>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Agent>());
        return agents;
    }
    private readonly List<AgentAssistSessionFiling> _filings = [];
    private readonly string _demo = $"demo-{Guid.NewGuid():N}";
    private readonly string _acme = $"acme-{Guid.NewGuid():N}";
    private ConnectionMultiplexer? _replicaA;
    private ConnectionMultiplexer? _replicaB;

    public AgentAssistLiveSessionStoreRedisTests(RedisFixture redis) => _redis = redis;

    private RedisAgentAssistLiveSessionStore StoreA => new(_replicaA!, _time);

    private RedisAgentAssistLiveSessionStore StoreB => new(_replicaB!, _time);

    private string DemoConversation => $"conv-of-{_demo}";

    private string AcmeConversation => $"conv-of-{_acme}";

    public async Task InitializeAsync()
    {
        _replicaA = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        _replicaB = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        foreach (var (tenant, id) in new[] { (_demo, DemoConversation), (_acme, AcmeConversation) })
        {
            _conversations.FindByVoiceLinkedIdAsync(new TenantId(tenant), LinkedId, Arg.Any<CancellationToken>()).Returns(new Conversation
            {
                ConversationId = EntityId.From(id), TenantId = new TenantId(tenant), ContactId = EntityId.New(),
                Channel = ChannelType.Voice, State = ConversationState.Active, CreatedAt = DateTimeOffset.UnixEpoch, VoiceLinkedId = LinkedId,
            });
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var filing in _filings)
            filing.Dispose();
        if (_replicaA is not null)
            await _replicaA.DisposeAsync();
        if (_replicaB is not null)
            await _replicaB.DisposeAsync();
    }

    private static CallSession Call(string? tenant) =>
        new("call-redis-1", LinkedId, "server-1", CallDirection.Inbound)
        {
            TenantId = tenant, AgentId = "agent-7", QueueName = "support", ConnectedAt = new DateTimeOffset(2026, 11, 17, 10, 0, 0, 250, TimeSpan.Zero),
        };

    private async Task<AgentAssistSessionFiling> StartOnAAsync(string sessionId, string startTimeTenant, CallSession call)
    {
        var filing = AgentAssistSessionFiling.Start(sessionId, startTimeTenant, call, StoreA, _time, NullLogger.Instance);
        _filings.Add(filing);
        await filing.WhenIdleAsync();
        return filing;
    }

    private async Task AdvanceAsync(TimeSpan by)
    {
        _time.Advance(by);
        foreach (var filing in _filings)
            await filing.WhenIdleAsync();
    }

    /// <summary>The list a supervisor of <paramref name="tenant"/> reads on a replica (<c>GET /supervisor/sessions/active</c>).</summary>
    private async Task<ActiveSessionDto[]> ListAsync(IAgentAssistLiveSessionStore store, string tenant) =>
        await SupervisorEndpoints.ToActiveSessionsAsync(
            await store.ListAsync(tenant, CancellationToken.None), new TenantId(tenant), _conversations, _agents, CancellationToken.None);

    private async Task<long> IndexLengthAsync(string tenant) =>
        await _replicaB!.GetDatabase().SortedSetLengthAsync(RedisAgentAssistLiveSessionStore.IndexKey(tenant));

    [Fact]
    public async Task List_ShouldShowTheSessionTheOtherReplicaFiled_WithEveryMember()
    {
        await StartOnAAsync("as-redis-shown", _demo, Call(_demo));

        var row = (await ListAsync(StoreB, _demo)).Should().ContainSingle("replica B reads what replica A filed").Subject;

        row.Should().Be(new ActiveSessionDto("as-redis-shown", "agent-7", null, "support", null,
            new DateTimeOffset(2026, 11, 17, 10, 0, 0, 250, TimeSpan.Zero), DemoConversation, null));
        (await ListAsync(StoreB, _acme)).Should().BeEmpty();
    }

    [Fact]
    public async Task List_ShouldShowTheLatestSentimentTheOtherReplicaFiled_WhenAReadingArrived()
    {
        string? latest = null;
        var filing = AgentAssistSessionFiling.Start("as-redis-sentiment", _demo, Call(_demo), StoreA, _time, NullLogger.Instance, () => latest);
        _filings.Add(filing);
        await filing.WhenIdleAsync();
        (await ListAsync(StoreB, _demo)).Should().ContainSingle().Which.Sentiment.Should().BeNull("no reading yet");

        latest = "Escalating";
        filing.OnEmission();
        await filing.WhenIdleAsync();

        (await ListAsync(StoreB, _demo)).Should().ContainSingle().Which.Sentiment.Should().Be("Escalating",
            "replica B reads the label replica A filed");
    }

    [Fact]
    public async Task List_ShouldDropTheRow_WhenTheFilingReplicaStopsHeartbeating()
    {
        var filing = await StartOnAAsync("as-redis-expired", _demo, Call(_demo));
        await AdvanceAsync(TimeSpan.FromSeconds(12));
        (await ListAsync(StoreB, _demo)).Should().ContainSingle("heartbeats keep it");

        filing.Dispose();
        _time.Advance(AgentAssistSessionFiling.RecordTtl);

        (await ListAsync(StoreB, _demo)).Should().BeEmpty("the record lives 15 s past its last heartbeat");
        (await IndexLengthAsync(_demo)).Should().Be(0, "the reader drops the expired index member");
    }

    /// <summary>
    /// Cross-replica read before the heartbeat, for both start-time tenants: replica A files a
    /// bound session under demo, its call is re-stamped acme, the session emits nothing, and replica B reads at once and
    /// after one 5 s heartbeat of A. With start-time tenant demo the two tenants conflict and the row goes to nobody; with
    /// none, acme owns the session and its row shows acme's conversation, never demo's.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task List_ShouldReachTheRestampsOwnerWithinOneHeartbeat_WhenTheStartTimeTenantIsDemoOrEmpty(bool startTimeTenantIsDemo)
    {
        var startTimeTenant = startTimeTenantIsDemo ? _demo : string.Empty;
        var call = Call(_demo);
        await StartOnAAsync("as-redis-restamp", startTimeTenant, call);

        call.TenantId = _acme;

        var atOnce = (await ListAsync(StoreB, _demo)).Should().ContainSingle(
            "until A's next heartbeat (at most 5 s) the row stays under the tenant that owned it").Subject;
        atOnce.ConversationId.Should().Be(DemoConversation, "with its members from before the re-stamp");
        (await ListAsync(StoreB, _acme)).Should().BeEmpty("never filed under the other tenant before A re-evaluates it");

        await AdvanceAsync(AgentAssistSessionFiling.HeartbeatInterval);

        (await ListAsync(StoreB, _demo)).Should().BeEmpty($"one 5 s heartbeat re-evaluates the owner (start-time tenant demo: {startTimeTenantIsDemo})");
        (await IndexLengthAsync(_demo)).Should().Be(0, "the former owner's index member is gone after the 5 s heartbeat");
        if (startTimeTenantIsDemo)
        {
            (await ListAsync(StoreB, _acme)).Should().BeEmpty("demo at start and acme now conflict: listed to nobody");
        }
        else
        {
            (await ListAsync(StoreB, _acme)).Should().ContainSingle("with no start-time tenant, acme owns the session")
                .Which.ConversationId.Should().Be(AcmeConversation, "acme's row never shows demo's conversation");
        }
    }

    /// <summary>One state of a live session, set up on replica A and mirrored in A's own registry.</summary>
    public enum State
    {
        Bound,
        LateStamp,
        ConflictAfterHeartbeat,
        Ownerless,
        OwnerChangeAfterHeartbeat,
        Ended,
    }

    /// <summary>
    /// The Redis-served list (read on replica B) and the in-process list (A's own registry, owner recomputed per read)
    /// answer the same rows for the same state once A has re-evaluated it (design D12).
    /// </summary>
    [Theory]
    [InlineData(State.Bound)]
    [InlineData(State.LateStamp)]
    [InlineData(State.ConflictAfterHeartbeat)]
    [InlineData(State.Ownerless)]
    [InlineData(State.OwnerChangeAfterHeartbeat)]
    [InlineData(State.Ended)]
    public async Task List_ShouldAnswerTheSameRowsFromRedisAndInProcess_WhenTheLeaderHasReevaluated(State state)
    {
        var sessionId = $"as-parity-{state}";
        var startTimeTenant = state is State.Bound or State.ConflictAfterHeartbeat or State.Ended ? _demo : string.Empty;
        var call = Call(state is State.LateStamp or State.Ownerless ? null : _demo);
        var registry = new OneSession(sessionId, startTimeTenant, call);
        var filing = await StartOnAAsync(sessionId, startTimeTenant, call);

        switch (state)
        {
            case State.LateStamp:
                call.TenantId = _demo;
                await AdvanceAsync(AgentAssistSessionFiling.BindCheckInterval);
                break;
            case State.ConflictAfterHeartbeat:
            case State.OwnerChangeAfterHeartbeat:
                call.TenantId = _acme;
                await AdvanceAsync(AgentAssistSessionFiling.HeartbeatInterval);
                break;
            case State.Ended:
                await filing.EndAsync();
                registry.Ended = true;
                break;
        }

        var inProcess = new InProcessAgentAssistLiveSessionStore(registry);
        foreach (var tenant in new[] { _demo, _acme })
        {
            (await ListAsync(StoreB, tenant)).Should().BeEquivalentTo(await ListAsync(inProcess, tenant),
                $"state {state}, tenant {(tenant == _demo ? "demo" : "acme")}: the shared list matches this replica's own");
        }
    }

    // ═══ supervisor-whisper-reaches-the-agent (design D2, task 1.2): FindAsync on replica B ═══

    [Fact]
    public async Task Find_ShouldReturnTheRecordTheOtherReplicaFiled_UnderItsOwnerTenantOnly()
    {
        await StartOnAAsync("as-find-shown", _demo, Call(_demo));

        var found = await StoreB.FindAsync(_demo, "as-find-shown", CancellationToken.None);

        found.Should().Be(LiveSessionRecord.Of("as-find-shown", _demo, Call(_demo)),
            "replica B reads what replica A filed");
        (await StoreB.FindAsync(_acme, "as-find-shown", CancellationToken.None)).Should().BeNull("another tenant never finds it");
        (await StoreB.FindAsync(_demo, "as-find-unknown", CancellationToken.None)).Should().BeNull("an unknown id is not found");
    }

    [Fact]
    public async Task Remove_ShouldRemoveOnlyTheCopyFiledWithTheExpiryGiven_WhenALaterFilingOfTheSessionFiledAgain()
    {
        const string sessionId = "as-remove-fenced";
        var record = LiveSessionRecord.Of(sessionId, _demo, Call(_demo));
        var first = _time.GetUtcNow() + AgentAssistSessionFiling.RecordTtl;
        await StoreA.FileAsync(record, first, CancellationToken.None);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var second = _time.GetUtcNow() + AgentAssistSessionFiling.RecordTtl;
        await StoreB.FileAsync(record, second, CancellationToken.None);   // a new generation of the session, on replica B

        await StoreA.RemoveAsync(_demo, sessionId, first, CancellationToken.None);

        (await StoreB.FindAsync(_demo, sessionId, CancellationToken.None)).Should().NotBeNull(
            "replica A's late removal names the copy it filed, and replica B has filed another since");

        await StoreB.RemoveAsync(_demo, sessionId, second, CancellationToken.None);

        (await StoreB.FindAsync(_demo, sessionId, CancellationToken.None)).Should().BeNull("the copy filed with that expiry is removed");
        (await IndexLengthAsync(_demo)).Should().Be(0, "with its index member");
    }

    [Fact]
    public async Task Find_ShouldReturnNull_WhenTheIndexMemberHasExpired()
    {
        var filing = await StartOnAAsync("as-find-expired", _demo, Call(_demo));
        filing.Dispose();   // replica A is gone: no more heartbeats
        _time.Advance(AgentAssistSessionFiling.RecordTtl - TimeSpan.FromMilliseconds(1));
        (await StoreB.FindAsync(_demo, "as-find-expired", CancellationToken.None)).Should().NotBeNull("precondition: not expired yet");

        _time.Advance(TimeSpan.FromMilliseconds(1));

        (await StoreB.FindAsync(_demo, "as-find-expired", CancellationToken.None)).Should().BeNull(
            "a member whose score is not strictly after now is not found, whatever Redis's own expiry does");
    }

    [Fact]
    public async Task Find_ShouldReturnNull_WhenTheHashExpiredAfterTheIndexRead()
    {
        await StartOnAAsync("as-find-nohash", _demo, Call(_demo));
        // The index member is live and the hash is gone, as when the hash's own TTL fires between the two reads.
        await _replicaB!.GetDatabase().KeyDeleteAsync(RedisAgentAssistLiveSessionStore.RecordKey(_demo, "as-find-nohash"));
        (await IndexLengthAsync(_demo)).Should().Be(1, "precondition: the index member is still there");

        (await StoreB.FindAsync(_demo, "as-find-nohash", CancellationToken.None)).Should().BeNull("an empty hash is not found");
    }

    /// <summary>
    /// The equivalence property of task 1.2: for every seeded state, FindAsync on replica B returns exactly the record
    /// ListAsync returns for that id, or null when ListAsync omits it, for both tenants.
    /// </summary>
    [Theory]
    [InlineData(State.Bound)]
    [InlineData(State.LateStamp)]
    [InlineData(State.ConflictAfterHeartbeat)]
    [InlineData(State.Ownerless)]
    [InlineData(State.OwnerChangeAfterHeartbeat)]
    [InlineData(State.Ended)]
    public async Task Find_ShouldReturnWhatListReturnsForThatId_ForEverySeededState(State state)
    {
        var sessionId = $"as-find-parity-{state}";
        var startTimeTenant = state is State.Bound or State.ConflictAfterHeartbeat or State.Ended ? _demo : string.Empty;
        var call = Call(state is State.LateStamp or State.Ownerless ? null : _demo);
        var filing = await StartOnAAsync(sessionId, startTimeTenant, call);

        switch (state)
        {
            case State.LateStamp:
                call.TenantId = _demo;
                await AdvanceAsync(AgentAssistSessionFiling.BindCheckInterval);
                break;
            case State.ConflictAfterHeartbeat:
            case State.OwnerChangeAfterHeartbeat:
                call.TenantId = _acme;
                await AdvanceAsync(AgentAssistSessionFiling.HeartbeatInterval);
                break;
            case State.Ended:
                await filing.EndAsync();
                break;
        }

        foreach (var tenant in new[] { _demo, _acme })
        {
            var listed = (await StoreB.ListAsync(tenant, CancellationToken.None)).SingleOrDefault(r => r.SessionId == sessionId);
            var found = await StoreB.FindAsync(tenant, sessionId, CancellationToken.None);
            found.Should().Be(listed, $"state {state}, tenant {(tenant == _demo ? "demo" : "acme")}: FindAsync is ListAsync for one id");
        }
    }

    /// <summary>The engine's registry on replica A, holding one session.</summary>
    private sealed class OneSession(string sessionId, string startTimeTenant, CallSession call) : IAgentAssistLiveSessions
    {
        public bool Ended { get; set; }

        public IReadOnlyList<LiveAssistSession> Snapshot() =>
            Ended ? [] : [AgentAssistLiveSessions.ToLive(sessionId, call, startTimeTenant)];

        public bool TryWhisper(string id, string text) => false;
    }
}
