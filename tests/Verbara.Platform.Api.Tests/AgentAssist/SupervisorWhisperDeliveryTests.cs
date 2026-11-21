using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// The tenant-scope host over a shared live-session store (<see cref="MemoryLiveSessionStore"/>, the Redis semantics),
/// on a <see cref="FakeTimeProvider"/>: what the whisper route of any Api replica reads.
/// </summary>
public sealed class SupervisorWhisperApiFactory : AgentAssistTenantScopeApiFactory
{
    public SupervisorWhisperApiFactory() => Monitor = new MemoryLiveSessionStore(Time);

    /// <summary>The clock of the live-session records and their filings.</summary>
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>The shared Monitor list every replica of this host reads.</summary>
    internal MemoryLiveSessionStore Monitor { get; }

    /// <inheritdoc />
    internal override IAgentAssistLiveSessionStore? LiveSessionStore => Monitor;
}

/// <summary>
/// supervisor-whisper-reaches-the-agent (spec <c>supervisor-silent-listen</c>, design D2-D7, task 2.1): the text whisper
/// <c>POST /api/v1/supervisor/sessions/{sessionId}/whisper</c> finds the session in the shared live-session store under
/// the caller's tenant, then publishes one <c>agentassist.suggestion</c> addressed to its bound agent and answers 204 No
/// Content (fixture <c>supervisor-whisper.v1.json</c> <c>responses."204"</c>); every refusal publishes nothing. A
/// recording subscriber on the host's <see cref="PlatformEventBus"/> sees every publish synchronously.
/// </summary>
public sealed class SupervisorWhisperDeliveryTests : IClassFixture<SupervisorWhisperApiFactory>, IDisposable
{
    private const string Acme = AgentAssistTenantScopeApiFactory.Acme;
    private const string Globex = AgentAssistTenantScopeApiFactory.Globex;
    private const string AgentX = "a-9e8d7c6b5a493827";

    private readonly SupervisorWhisperApiFactory _factory;
    private readonly List<AgentAssistSuggestionEvent> _published = [];
    private readonly IDisposable _recorder;
    private readonly List<AgentAssistSessionFiling> _filings = [];

    public SupervisorWhisperDeliveryTests(SupervisorWhisperApiFactory factory)
    {
        _factory = factory;
        _recorder = factory.Services.GetRequiredService<PlatformEventBus>().Events.Subscribe(evt =>
        {
            if (evt is AgentAssistSuggestionEvent suggestion)
            {
                lock (_published)
                    _published.Add(suggestion);
            }
        });
    }

    public void Dispose()
    {
        _recorder.Dispose();
        foreach (var filing in _filings)
            filing.Dispose();
    }

    private static string NewId(string label) => $"as-sw-{label}-{Guid.NewGuid():N}";

    private static string WhisperRoute(string sessionId) => $"/api/v1/supervisor/sessions/{sessionId}/whisper";

    private IReadOnlyList<AgentAssistSuggestionEvent> PublishedFor(string sessionId)
    {
        lock (_published)
            return [.. _published.Where(e => e.SessionId == sessionId)];
    }

    /// <summary>Saves a tracked voice conversation of <paramref name="tenant"/> for the call <paramref name="linkedId"/>.</summary>
    private async Task<string> TrackConversationAsync(string tenant, string linkedId)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = new TenantId(tenant),
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            Channel = ChannelType.Voice,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            VoiceLinkedId = linkedId,
        };
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);
        return conversation.ConversationId.Value;
    }

    /// <summary>Files a live record of <paramref name="tenant"/> for 15 s, with its conversation tracked unless told not to.</summary>
    private async Task<(string SessionId, string? ConversationId)> FileSessionAsync(
        string tenant, string label, string? agentId = AgentX, bool withLinkedId = true, bool trackConversation = true)
    {
        var sessionId = NewId(label);
        var linkedId = withLinkedId ? $"{sessionId}.call" : null;
        string? conversationId = null;
        if (linkedId is not null && trackConversation)
            conversationId = await TrackConversationAsync(tenant, linkedId);

        var record = new LiveSessionRecord(sessionId, tenant, agentId, "support", "5551234", null, linkedId);
        await _factory.Monitor.FileAsync(record, _factory.Time.GetUtcNow() + AgentAssistSessionFiling.RecordTtl, CancellationToken.None);
        return (sessionId, conversationId);
    }

    private async Task<AgentAssistSessionFiling> StartFilingAsync(string sessionId, string startTimeTenant, CallSession call)
    {
        var filing = AgentAssistSessionFiling.Start(sessionId, startTimeTenant, call, _factory.Monitor, _factory.Time, NullLogger.Instance);
        _filings.Add(filing);
        await filing.WhenIdleAsync();
        return filing;
    }

    private async Task<HttpResponseMessage> WhisperAsync(string tenant, string sessionId, object body)
    {
        using var client = _factory.SupervisorClient(tenant);
        return await client.PostAsJsonAsync(WhisperRoute(sessionId), body);
    }

    private static async Task ShouldBeRefusalAsync(HttpResponseMessage response, HttpStatusCode status, string? error)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, because: $"the response was {body}");
        if (error is null)
            body.Should().BeEmpty("this refusal has no body");
        else
            JsonNode.Parse(body)!["error"]!.GetValue<string>().Should().Be(error);
    }

    private static async Task ShouldBeNoContentAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, because: $"the response was {body}");
        body.Should().BeEmpty("204 No Content, never an empty 200");
    }

    // ═══ Accepted ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PostWhisper_ShouldAnswer204AndPublishOneSuggestionForTheBoundAgent_WhenTheSessionIsTheCallersAndBound()
    {
        var (sessionId, conversationId) = await FileSessionAsync(Acme, "own");

        using var response = await WhisperAsync(Acme, sessionId, new { text = "  Offer the retention discount now. " });

        await ShouldBeNoContentAsync(response);
        var evt = PublishedFor(sessionId).Should().ContainSingle("exactly one event per whisper").Subject;
        evt.SessionId.Should().Be(sessionId);
        evt.AgentId.Should().Be(AgentX);
        evt.ConversationId.Should().Be(conversationId);
        evt.TenantId.Should().Be(Acme);
        evt.Text.Should().Be("Offer the retention discount now.", "the text is trimmed");
        evt.Priority.Should().Be("Critical");
        evt.Source.Should().Be("Supervisor");
        evt.TriggerPhrase.Should().BeNull();
        evt.SuggestionId.Should().StartWith("sw-");
        evt.Audience.Should().Be(ConversationAudience.ForAgent(AgentX));
    }

    [Fact]
    public async Task PostWhisper_ShouldIgnoreTheBodysAgentAndConversation_WhenTheRequestNamesThem()
    {
        var (sessionId, conversationId) = await FileSessionAsync(Acme, "ignored");

        using var response = await WhisperAsync(Acme, sessionId, new { text = "hi", agentId = "Y", conversationId = "Z" });

        await ShouldBeNoContentAsync(response);
        var evt = PublishedFor(sessionId).Should().ContainSingle().Subject;
        evt.AgentId.Should().Be(AgentX, "the session's bound agent, never the body's");
        evt.ConversationId.Should().Be(conversationId, "the session's bound conversation, never the body's");
    }

    /// <summary>Runs the session on this replica too, as its engine's replica does, with its owner rule computed live.</summary>
    private void RunHere(string sessionId, string callTenant, string startTimeTenant, bool deliversWhispers = true, bool throwsOnWhisper = false) =>
        _factory.Live.Add(
            AgentAssistLiveSessions.ToLive(sessionId, Call(sessionId, callTenant), startTimeTenant), deliversWhispers, throwsOnWhisper);

    [Fact]
    public async Task PostWhisper_ShouldAnswer204AndPublish_WhenTheSpokenWhisperIsDisabled()
    {
        var (sessionId, _) = await FileSessionAsync(Acme, "spoken-off");
        RunHere(sessionId, Acme, Acme, deliversWhispers: false);   // Pro's spoken whisper is off: TryWhisper answers false

        using var response = await WhisperAsync(Acme, sessionId, new { text = "offer the refund" });

        await ShouldBeNoContentAsync(response);
        PublishedFor(sessionId).Should().ContainSingle("the text whisper does not depend on the spoken one");
        _factory.Live.Whispers.Should().ContainSingle(w => w.SessionId == sessionId, "the spoken whisper is still attempted, best effort")
            .Which.Text.Should().Be("offer the refund");
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswer204AndAttemptNoSpokenWhisper_WhenTheEngineRunsOnAnotherReplica()
    {
        var (sessionId, _) = await FileSessionAsync(Acme, "elsewhere");

        using var response = await WhisperAsync(Acme, sessionId, new { text = "offer the refund" });

        await ShouldBeNoContentAsync(response);
        PublishedFor(sessionId).Should().ContainSingle();
        _factory.Live.Whispers.Should().NotContain(w => w.SessionId == sessionId, "this replica runs no such session");
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswer204AndPublishOnce_WhenTheSpokenWhisperThrows()
    {
        var (sessionId, _) = await FileSessionAsync(Acme, "spoken-throws");
        RunHere(sessionId, Acme, Acme, throwsOnWhisper: true);

        using var response = await WhisperAsync(Acme, sessionId, new { text = "offer the refund" });

        await ShouldBeNoContentAsync(response);
        PublishedFor(sessionId).Should().ContainSingle("204 means published: a failed spoken attempt never turns it into a 500");
        _factory.Live.Whispers.Should().ContainSingle(w => w.SessionId == sessionId, "precondition: the spoken whisper was attempted");
    }

    [Fact]
    public async Task PostWhisper_ShouldQueueNoSpokenWhisper_WhenTheLiveOwnerConflictsAndTheRecordIsStillFiledUnderTheCaller()
    {
        var (sessionId, _) = await FileSessionAsync(Acme, "spoken-conflict");
        RunHere(sessionId, callTenant: Globex, startTimeTenant: Acme);   // the owner rule now finds no owner

        using var response = await WhisperAsync(Acme, sessionId, new { text = "offer the refund" });

        await ShouldBeNoContentAsync(response);
        PublishedFor(sessionId).Should().ContainSingle("the record still filed under the caller is found until the heartbeat");
        _factory.Live.Whispers.Should().NotContain(w => w.SessionId == sessionId,
            "the spoken whisper reaches only a session the caller's tenant owns as it is now");
    }

    [Fact]
    public async Task PostWhisper_ShouldGiveEachWhisperItsOwnSuggestionId()
    {
        var (sessionId, _) = await FileSessionAsync(Acme, "two");

        using (var first = await WhisperAsync(Acme, sessionId, new { text = "one" }))
            await ShouldBeNoContentAsync(first);
        using (var second = await WhisperAsync(Acme, sessionId, new { text = "two" }))
            await ShouldBeNoContentAsync(second);

        PublishedFor(sessionId).Select(e => e.SuggestionId).Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswer204_WhenTheTextIsExactly500Characters()
    {
        var (sessionId, _) = await FileSessionAsync(Acme, "500");

        using var response = await WhisperAsync(Acme, sessionId, new { text = "  " + new string('a', 500) + " " });

        await ShouldBeNoContentAsync(response);
        PublishedFor(sessionId).Should().ContainSingle().Which.Text.Should().HaveLength(500);
    }

    // ═══ 404: not the caller's, or not live ═════════════════════════════════════

    [Fact]
    public async Task PostWhisper_ShouldAnswer404AndPublishNothing_WhenTheSessionBelongsToAnotherTenant()
    {
        var (sessionId, _) = await FileSessionAsync(Globex, "foreign");

        using var response = await WhisperAsync(Acme, sessionId, new { text = "offer the refund" });

        await ShouldBeRefusalAsync(response, HttpStatusCode.NotFound, error: null);
        PublishedFor(sessionId).Should().BeEmpty();
        _factory.Live.Whispers.Should().NotContain(w => w.SessionId == sessionId);
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswer404NotTheTextRefusal_WhenAnotherTenantsSessionGetsAnEmptyText()
    {
        var (sessionId, _) = await FileSessionAsync(Globex, "foreign-empty");

        using var response = await WhisperAsync(Acme, sessionId, new { text = "" });

        await ShouldBeRefusalAsync(response, HttpStatusCode.NotFound, error: null);
        PublishedFor(sessionId).Should().BeEmpty();
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswer404AndPublishNothing_WhenTheIdNamesNoLiveSession()
    {
        var unknown = NewId("unknown");

        using var response = await WhisperAsync(Acme, unknown, new { text = "offer the refund" });

        await ShouldBeRefusalAsync(response, HttpStatusCode.NotFound, error: null);
        PublishedFor(unknown).Should().BeEmpty();
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswer404AndPublishNothing_WhenTheSessionHasEnded()
    {
        var sessionId = NewId("ended");
        await TrackConversationAsync(Acme, $"{sessionId}.call");
        var filing = await StartFilingAsync(sessionId, Acme, Call(sessionId, Acme));
        await filing.EndAsync();

        using var response = await WhisperAsync(Acme, sessionId, new { text = "offer the refund" });

        await ShouldBeRefusalAsync(response, HttpStatusCode.NotFound, error: null);
        PublishedFor(sessionId).Should().BeEmpty();
    }

    // ═══ 404 bounded by the store's refresh (spec audit round 1, F2) ═══════════

    private static CallSession Call(string sessionId, string tenant) =>
        new($"call-{sessionId}", $"{sessionId}.call", "server-1", CallDirection.Inbound)
        {
            TenantId = tenant, AgentId = AgentX, QueueName = "support",
        };

    [Fact]
    public async Task PostWhisper_ShouldReachTheFormerOwnerOnlyUntilTheReevaluation_WhenTheSessionsTenantsComeIntoConflict()
    {
        var sessionId = NewId("conflict");
        var acmeConversation = await TrackConversationAsync(Acme, $"{sessionId}.call");
        await TrackConversationAsync(Globex, $"{sessionId}.call");
        var call = Call(sessionId, Acme);
        var filing = await StartFilingAsync(sessionId, Acme, call);

        call.TenantId = Globex;   // the call's tenant now conflicts with the start-time tenant; no event yet

        using (var acme = await WhisperAsync(Acme, sessionId, new { text = "in the window" }))
            await ShouldBeNoContentAsync(acme);
        using (var globex = await WhisperAsync(Globex, sessionId, new { text = "in the window" }))
            await ShouldBeRefusalAsync(globex, HttpStatusCode.NotFound, error: null);
        PublishedFor(sessionId).Should().ContainSingle("the record still filed under the former owner is found by it only")
            .Which.Should().Match<AgentAssistSuggestionEvent>(e => e.TenantId == Acme && e.ConversationId == acmeConversation);

        _factory.Time.Advance(AgentAssistSessionFiling.HeartbeatInterval);
        await filing.WhenIdleAsync();

        using (var acme = await WhisperAsync(Acme, sessionId, new { text = "after the heartbeat" }))
            await ShouldBeRefusalAsync(acme, HttpStatusCode.NotFound, error: null);
        using (var globex = await WhisperAsync(Globex, sessionId, new { text = "after the heartbeat" }))
            await ShouldBeRefusalAsync(globex, HttpStatusCode.NotFound, error: null);
        PublishedFor(sessionId).Should().ContainSingle("nothing more is published once the re-evaluation removed the record");
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswer404FromTheRecordsExpiry_WhenTheEngineReplicaStopped()
    {
        var sessionId = NewId("expired");
        await TrackConversationAsync(Acme, $"{sessionId}.call");
        var filing = await StartFilingAsync(sessionId, Acme, Call(sessionId, Acme));
        filing.Dispose();   // the engine's replica stopped: the record is no longer re-filed

        _factory.Time.Advance(AgentAssistSessionFiling.RecordTtl - TimeSpan.FromSeconds(1));
        using (var before = await WhisperAsync(Acme, sessionId, new { text = "before expiry" }))
            await ShouldBeNoContentAsync(before);

        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        using (var after = await WhisperAsync(Acme, sessionId, new { text = "after expiry" }))
            await ShouldBeRefusalAsync(after, HttpStatusCode.NotFound, error: null);

        PublishedFor(sessionId).Select(e => e.Text).Should().Equal(["before expiry"], "nothing is published from the expiry on");
    }

    // ═══ 400: invalid text ═════════════════════════════════════════════════════

    public static TheoryData<string, string> InvalidTexts() => new()
    {
        { "blank", """{"text":"   "}""" },
        { "empty", """{"text":""}""" },
        { "missing", "{}" },
        { "null", """{"text":null}""" },
        { "501-characters", $$"""{"text":"{{new string('a', 501)}}"}""" },
    };

    [Theory]
    [MemberData(nameof(InvalidTexts))]
    public async Task PostWhisper_ShouldAnswer400WhisperTextInvalidAndPublishNothing_WhenTheTextIs(string label, string json)
    {
        var (sessionId, _) = await FileSessionAsync(Acme, $"text-{label}");
        using var client = _factory.SupervisorClient(Acme);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(WhisperRoute(sessionId), content);

        await ShouldBeRefusalAsync(response, HttpStatusCode.BadRequest, "whisper-text-invalid");
        PublishedFor(sessionId).Should().BeEmpty();
        _factory.Live.Whispers.Should().NotContain(w => w.SessionId == sessionId, "nothing is attempted");
    }

    // ═══ 409: not bound ════════════════════════════════════════════════════════

    public enum Unbound
    {
        NoAgent,
        NoLinkedId,
        NoConversation,
    }

    [Theory]
    [InlineData(Unbound.NoAgent)]
    [InlineData(Unbound.NoLinkedId)]
    [InlineData(Unbound.NoConversation)]
    public async Task PostWhisper_ShouldAnswer409WhisperSessionNotBoundAndPublishNothing_WhenTheSessionHas(Unbound unbound)
    {
        var (sessionId, _) = await FileSessionAsync(
            Acme,
            $"unbound-{unbound}",
            agentId: unbound == Unbound.NoAgent ? null : AgentX,
            withLinkedId: unbound != Unbound.NoLinkedId,
            trackConversation: unbound != Unbound.NoConversation);

        using var response = await WhisperAsync(Acme, sessionId, new { text = "offer the refund" });

        await ShouldBeRefusalAsync(response, HttpStatusCode.Conflict, "whisper-session-not-bound");
        PublishedFor(sessionId).Should().BeEmpty("nothing is published, so no read-all subscriber receives it either");
        _factory.Live.Whispers.Should().NotContain(w => w.SessionId == sessionId, "nothing is attempted");
    }
}
