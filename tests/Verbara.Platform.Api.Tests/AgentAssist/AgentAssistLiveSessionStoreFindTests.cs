using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// supervisor-whisper-reaches-the-agent (design D2, tasks 1.1 and 1.3): <see cref="IAgentAssistLiveSessionStore.FindAsync"/>
/// finds one live session's record under the caller's tenant only, with the semantics <see cref="IAgentAssistLiveSessionStore.ListAsync"/>
/// applies to that member. Covered on the in-process store (the engine's registry, owner recomputed on every read) and on
/// the shared-store test double <see cref="MemoryLiveSessionStore"/> (the Redis semantics: an index by expiry, filed by
/// <see cref="AgentAssistSessionFiling"/>).
/// </summary>
public sealed class AgentAssistLiveSessionStoreFindTests : IDisposable
{
    private const string Acme = "acme";
    private const string Globex = "globex";
    private const string SessionId = "as-find-1";
    private const string LinkedId = "1712345678.80";
    private const string AgentGuid = "0651f84b6f284a1c9b8d658330fa8310";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));
    private readonly List<AgentAssistSessionFiling> _filings = [];

    public void Dispose()
    {
        foreach (var filing in _filings)
            filing.Dispose();
    }

    private static CallSession Call(string? tenant) =>
        new("call-find-1", LinkedId, "server-1", CallDirection.Inbound)
        {
            TenantId = tenant, AgentId = AgentGuid, QueueName = "support",
        };

    private static Task<LiveSessionRecord?> FindAsync(IAgentAssistLiveSessionStore store, string tenant, string sessionId = SessionId) =>
        store.FindAsync(tenant, sessionId, CancellationToken.None);

    private async Task<AgentAssistSessionFiling> FileAsync(MemoryLiveSessionStore store, string startTimeTenant, CallSession call)
    {
        var filing = AgentAssistSessionFiling.Start(SessionId, startTimeTenant, call, store, _time, NullLogger.Instance);
        _filings.Add(filing);
        await filing.WhenIdleAsync();
        return filing;
    }

    // ═══ In-process store (task 1.1) ═════════════════════════════════════════════

    [Fact]
    public async Task InProcessFind_ShouldReturnTheRecord_WhenTheSessionIsOwnedByTheCallersTenant()
    {
        var live = new FakeLiveSessions();
        var call = Call(Acme);
        live.Add(AgentAssistLiveSessions.ToLive(SessionId, call, Acme, "Frustrated"));
        var store = new InProcessAgentAssistLiveSessionStore(live);

        var found = await FindAsync(store, Acme);

        found.Should().Be(LiveSessionRecord.Of(SessionId, Acme, call, "Frustrated"), "the record ListAsync maps for the same session");
        found!.AgentId.Should().Be(AgentGuid);
        found.LinkedId.Should().Be(LinkedId);
    }

    [Fact]
    public async Task InProcessFind_ShouldReturnNull_WhenTheSessionIsOwnedByAnotherTenant()
    {
        var live = new FakeLiveSessions();
        live.Add(AgentAssistLiveSessions.ToLive(SessionId, Call(Globex), Globex));
        var store = new InProcessAgentAssistLiveSessionStore(live);

        (await FindAsync(store, Acme)).Should().BeNull("another tenant's session is not found");
        (await FindAsync(store, Globex)).Should().NotBeNull("precondition: its own tenant finds it");
    }

    [Theory]
    [InlineData(null, "")]      // ownerless: neither the call nor the start-time snapshot names a tenant
    [InlineData(Acme, Globex)]  // conflicted: the call and the start-time snapshot disagree
    public async Task InProcessFind_ShouldReturnNull_WhenTheSessionIsOwnerlessOrConflicted(string? callTenant, string sessionTenant)
    {
        var live = new FakeLiveSessions();
        live.Add(AgentAssistLiveSessions.ToLive(SessionId, Call(callTenant), sessionTenant));
        var store = new InProcessAgentAssistLiveSessionStore(live);

        (await FindAsync(store, Acme)).Should().BeNull();
        (await FindAsync(store, Globex)).Should().BeNull();
    }

    [Fact]
    public async Task InProcessFind_ShouldReturnNull_WhenTheIdNamesNoLiveSession()
    {
        var live = new FakeLiveSessions();
        live.Add(AgentAssistLiveSessions.ToLive(SessionId, Call(Acme), Acme));
        var store = new InProcessAgentAssistLiveSessionStore(live);

        (await FindAsync(store, Acme, "as-find-unknown")).Should().BeNull("an ended or unknown session is not in the registry");
    }

    [Fact]
    public async Task InProcessFind_ShouldRecomputeTheOwner_WhenTheCallIsRestampedBetweenReads()
    {
        var call = Call(Acme);
        var registry = new RecomputingRegistry(SessionId, call, string.Empty);
        var store = new InProcessAgentAssistLiveSessionStore(registry);
        (await FindAsync(store, Acme)).Should().NotBeNull("precondition");

        call.TenantId = Globex;

        (await FindAsync(store, Acme)).Should().BeNull("the owner is recomputed on every read: no window");
        (await FindAsync(store, Globex)).Should().NotBeNull();
    }

    // ═══ Shared store test double (task 1.1) ═════════════════════════════════════

    [Fact]
    public async Task MemoryFind_ShouldReturnTheFiledRecord_WhenFiledUnderTheCallersTenant()
    {
        var store = new MemoryLiveSessionStore(_time);
        var call = Call(Acme);
        await FileAsync(store, Acme, call);

        var found = await FindAsync(store, Acme);

        found.Should().Be(LiveSessionRecord.Of(SessionId, Acme, call));
        (await store.ListAsync(Acme, CancellationToken.None)).Should().Equal([found!], "FindAsync returns the record ListAsync returns");
    }

    [Fact]
    public async Task MemoryFind_ShouldReturnNull_WhenFiledUnderAnotherTenant()
    {
        var store = new MemoryLiveSessionStore(_time);
        await FileAsync(store, Globex, Call(Globex));

        (await FindAsync(store, Acme)).Should().BeNull();
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(Acme, Globex)]
    public async Task MemoryFind_ShouldReturnNull_WhenTheSessionIsOwnerlessOrConflicted(string? callTenant, string sessionTenant)
    {
        var store = new MemoryLiveSessionStore(_time);
        await FileAsync(store, sessionTenant, Call(callTenant));

        (await FindAsync(store, Acme)).Should().BeNull("never filed");
        (await FindAsync(store, Globex)).Should().BeNull("never filed");
    }

    [Fact]
    public async Task MemoryFind_ShouldReturnNull_AfterTheRecordIsRemoved()
    {
        var store = new MemoryLiveSessionStore(_time);
        var filing = await FileAsync(store, Acme, Call(Acme));
        (await FindAsync(store, Acme)).Should().NotBeNull("precondition");

        await filing.EndAsync();

        (await FindAsync(store, Acme)).Should().BeNull("an ended session's record is removed");
    }

    [Fact]
    public async Task MemoryFind_ShouldReturnNull_FromTheRecordsExpiry_WhenTheFilingReplicaStops()
    {
        var store = new MemoryLiveSessionStore(_time);
        var filing = await FileAsync(store, Acme, Call(Acme));
        filing.Dispose();   // the engine's replica is gone: no more heartbeats

        _time.Advance(AgentAssistSessionFiling.RecordTtl - TimeSpan.FromMilliseconds(1));
        (await FindAsync(store, Acme)).Should().NotBeNull("the record lives until its last filing + 15 s");

        _time.Advance(TimeSpan.FromMilliseconds(1));
        (await FindAsync(store, Acme)).Should().BeNull("an index member is found only while its expiry is strictly after now");
    }

    // ═══ Conflict window (task 1.3, design Risks) ════════════════════════════════

    [Fact]
    public async Task MemoryFind_ShouldFindTheRecordUnderTheFormerOwnerOnly_UntilTheLeaderReevaluatesTheConflict()
    {
        var store = new MemoryLiveSessionStore(_time);
        var call = Call(Acme);
        var filing = await FileAsync(store, Acme, call);

        call.TenantId = Globex;   // the call's tenant now conflicts with the start-time tenant; no event yet

        (await FindAsync(store, Acme)).Should().NotBeNull("until the next heartbeat the record stays filed under its former owner");
        (await FindAsync(store, Globex)).Should().BeNull("and is never found by the other tenant");

        _time.Advance(AgentAssistSessionFiling.HeartbeatInterval);
        await filing.WhenIdleAsync();

        (await FindAsync(store, Acme)).Should().BeNull("the heartbeat's re-evaluation removed it");
        (await FindAsync(store, Globex)).Should().BeNull("a conflicted session is found by neither tenant");
    }

    /// <summary>The engine's registry holding one session, its owner recomputed from the call on every snapshot.</summary>
    private sealed class RecomputingRegistry(string sessionId, CallSession call, string sessionTenant) : IAgentAssistLiveSessions
    {
        public IReadOnlyList<LiveAssistSession> Snapshot() => [AgentAssistLiveSessions.ToLive(sessionId, call, sessionTenant)];

        public bool TryWhisper(string id, string text) => false;
    }
}
