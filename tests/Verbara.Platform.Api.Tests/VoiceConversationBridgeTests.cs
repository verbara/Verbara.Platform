using System.Globalization;
using System.Reactive.Subjects;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Unit tests for <see cref="VoiceConversationBridge"/>. The handlers are driven directly via
/// the internal <c>HandleEventAsync</c> for determinism (no fire-and-forget timing).
///
/// <para><b>Tenant resolution note:</b> the AMI <c>GetVar</c> tenant-resolution wire (read the
/// trunk leg's <c>TENANT_ID</c> + stamp it) requires a live <see cref="CallSession"/> participant,
/// and <c>CallSession.AddParticipant</c> is <c>internal</c> to the SDK — so that wire is verified
/// in the lab harness. These tests exercise the bridge's decision logic by pre-stamping
/// <see cref="CallSession.TenantId"/> (the resolver short-circuits to it) and the fail-closed
/// branch (unresolved tenant ⇒ no Conversation).</para>
/// </summary>
public sealed class VoiceConversationBridgeTests : IDisposable
{
    private const string Tenant = "acme";
    private const string AgentGuid = "11111111111111111111111111111111";
    private static readonly string AgentInterface = $"PJSIP/{Tenant}-agent-{AgentGuid}";

    private readonly ICallSessionManager _sessions = Substitute.For<ICallSessionManager>();
    private readonly VerbaraServerPool _serverPool =
        new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IContactIdentityResolver _contacts = Substitute.For<IContactIdentityResolver>();
    private readonly IContactStore _contactStore = Substitute.For<IContactStore>();
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly IQueueStore _queues = Substitute.For<IQueueStore>();
    private readonly IAgentCapacityService _capacity = Substitute.For<IAgentCapacityService>();
    private readonly PlatformEventBus _eventBus = new();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAmiConnection _ami = Substitute.For<IAmiConnection>();

    public VoiceConversationBridgeTests()
    {
        _sessions.Events.Returns(new Subject<SessionDomainEvent>());
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero));
        // Queue lookup for the screen-pop auto-answer default is best-effort; default to "no queues".
        _queues.ListAsync(Arg.Any<TenantId>(), Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Queue>([], 0, 1, 500));
    }

    private VoiceConversationBridge CreateBridge(bool isLeader = true) =>
        new(_sessions, _serverPool, _conversations, _contacts, _contactStore, _agents, _queues, _capacity, _eventBus,
            LeaderStub(isLeader), _clock, NullLogger<VoiceConversationBridge>.Instance);

    private static IClusterLeader LeaderStub(bool isLeader)
    {
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(isLeader);
        leader.Resource.Returns(VoiceLeaderResources.AmiOwner);
        return leader;
    }

    private void AddPrimaryServer() =>
        _serverPool.AddExistingServer("primary", new VerbaraServer(_ami, NullLogger<VerbaraServer>.Instance));

    private static CallSession MakeSession(string linkedId, string? tenant, string? agentInterface = null)
    {
        var session = new CallSession($"s-{linkedId}", linkedId, "primary", CallDirection.Inbound);
        if (tenant is not null) session.TenantId = tenant;
        if (agentInterface is not null) session.AgentInterface = agentInterface;
        return session;
    }

    private Contact StubContact()
    {
        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = new TenantId(Tenant),
            CreatedAt = _clock.UtcNow,
        };
        _contacts.ResolveAsync(Arg.Any<TenantId>(), Arg.Any<ChannelAddress>(), Arg.Any<CancellationToken>())
            .Returns(contact);
        return contact;
    }

    private static Conversation VoiceConversation(string linkedId, ConversationState state) =>
        new()
        {
            ConversationId = EntityId.New(),
            TenantId = new TenantId(Tenant),
            ContactId = EntityId.New(),
            Channel = ChannelType.Voice,
            State = state,
            CreatedAt = new DateTimeOffset(2026, 5, 31, 11, 0, 0, TimeSpan.Zero),
            VoiceLinkedId = linkedId,
        };

    private static Agent AgentInState(AgentState state) =>
        new()
        {
            AgentId = EntityId.From(AgentGuid),
            TenantId = new TenantId(Tenant),
            UserId = EntityId.New(),
            DisplayName = "Ana",
            State = state,
            CreatedAt = new DateTimeOffset(2026, 5, 31, 9, 0, 0, TimeSpan.Zero),
        };

    private List<PlatformEvent> CaptureEvents()
    {
        var captured = new List<PlatformEvent>();
        _eventBus.Events.Subscribe(captured.Add);
        return captured;
    }

    // ─── Leader gate ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleEventAsync_ShouldDoNothing_WhenNotLeader()
    {
        var bridge = CreateBridge(isLeader: false);

        await bridge.HandleEventAsync(new CallQueuedEvent("s-1", "primary", _clock.UtcNow, "acme-Support", 1));

        _sessions.DidNotReceive().GetById(Arg.Any<string>());
        await _conversations.DidNotReceive().SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
    }

    // ─── Create (Queued) ────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleEventAsync_ShouldCreateVoiceConversation_WhenQueuedAndTenantResolved()
    {
        var session = MakeSession("link-1", Tenant);
        _sessions.GetById("s-link-1").Returns(session);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-1", Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        var contact = StubContact();
        var events = CaptureEvents();
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallQueuedEvent("s-link-1", "primary", _clock.UtcNow, "acme-Support", 1));

        await _conversations.Received(1).SaveAsync(
            Arg.Is<Conversation>(c =>
                c.Channel == ChannelType.Voice &&
                c.TenantId.Value == Tenant &&
                c.VoiceLinkedId == "link-1" &&
                c.ContactId == contact.ContactId &&
                c.State == ConversationState.Queued),
            Arg.Any<CancellationToken>());
        // No participant on the test session ⇒ null CallerIdNum ⇒ documented 'anonymous' voice contact.
        await _contacts.Received(1).ResolveAsync(
            Arg.Is<TenantId>(t => t.Value == Tenant),
            Arg.Is<ChannelAddress>(a => a.Channel == ChannelType.Voice && a.Address == "anonymous"),
            Arg.Any<CancellationToken>());
        events.OfType<ConversationStateChangedEvent>()
            .Should().Contain(ev => ev.OldState == "" && ev.NewState == nameof(ConversationState.Queued));
    }

    [Fact]
    public async Task HandleEventAsync_ShouldNotCreateConversation_WhenTenantUnresolved()
    {
        // No pre-stamped tenant + empty server pool ⇒ tenant cannot be resolved ⇒ fail-closed.
        var session = MakeSession("link-2", tenant: null);
        _sessions.GetById("s-link-2").Returns(session);
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallQueuedEvent("s-link-2", "primary", _clock.UtcNow, "acme-Support", 1));

        await _conversations.DidNotReceive().SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
        await _contacts.DidNotReceive().ResolveAsync(Arg.Any<TenantId>(), Arg.Any<ChannelAddress>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleEventAsync_ShouldBeIdempotent_WhenConversationAlreadyExistsForCall()
    {
        var session = MakeSession("link-3", Tenant);
        _sessions.GetById("s-link-3").Returns(session);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-3", Arg.Any<CancellationToken>())
            .Returns(VoiceConversation("link-3", ConversationState.Queued));
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallQueuedEvent("s-link-3", "primary", _clock.UtcNow, "acme-Support", 1));

        await _conversations.DidNotReceive().SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
        await _contacts.DidNotReceive().ResolveAsync(Arg.Any<TenantId>(), Arg.Any<ChannelAddress>(), Arg.Any<CancellationToken>());
    }

    // ─── Connected (answer) ───────────────────────────────────────────────────

    [Fact]
    public async Task HandleEventAsync_ShouldActivateAssignOwnerReserveAndGoBusy_WhenConnected()
    {
        var session = MakeSession("link-4", Tenant, AgentInterface);
        _sessions.GetById("s-link-4").Returns(session);
        var conversation = VoiceConversation("link-4", ConversationState.Queued);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-4", Arg.Any<CancellationToken>())
            .Returns(conversation);
        var agent = AgentInState(AgentState.Available);
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(agent);
        var screenPopContact = new Contact
        {
            ContactId = conversation.ContactId,
            TenantId = new TenantId(Tenant),
            FirstName = "Ada",
            LastName = "Lovelace",
            CreatedAt = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero),
        };
        _contactStore.GetByIdAsync(Arg.Any<TenantId>(), conversation.ContactId, Arg.Any<CancellationToken>())
            .Returns(screenPopContact);
        var events = CaptureEvents();
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallConnectedEvent("s-link-4", "primary", _clock.UtcNow, null, "acme-Support", TimeSpan.FromSeconds(3)));

        conversation.State.Should().Be(ConversationState.Active);
        conversation.Owner.Should().NotBeNull();
        conversation.Owner!.Kind.Should().Be(ConversationOwnerKind.Agent);
        conversation.Owner!.OwnerId!.Value.Value.Should().Be(AgentGuid);
        await _capacity.Received(1).ReserveAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), ChannelType.Voice, Arg.Any<CancellationToken>());
        agent.State.Should().Be(AgentState.Busy);
        await _agents.Received(1).SaveAsync(Arg.Is<Agent>(a => a.State == AgentState.Busy), Arg.Any<CancellationToken>());
        events.OfType<AgentStateChangedEvent>().Should().Contain(ev => ev.NewState == nameof(AgentState.Busy));
        events.OfType<ConversationStateChangedEvent>()
            .Should().Contain(ev => ev.OldState == nameof(ConversationState.Queued) && ev.NewState == nameof(ConversationState.Active));
        // 3B.1 screen-pop: agent-targeted event with the resolved contact + correlation keys.
        events.OfType<VoiceScreenPopEvent>().Should().ContainSingle(ev =>
            ev.AgentId == AgentGuid &&
            ev.ConversationId == conversation.ConversationId.Value &&
            ev.Channel == nameof(ChannelType.Voice) &&
            ev.ContactId == conversation.ContactId.Value &&
            ev.ContactName == "Ada Lovelace" &&
            ev.VoiceLinkedId == "link-4");
    }

    // ─── Ended (hangup) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleEventAsync_ShouldWrapUpReleaseAndGoAcw_WhenEndedAfterAnswer()
    {
        var session = MakeSession("link-5", Tenant, AgentInterface);
        _sessions.GetById("s-link-5").Returns(session);
        var conversation = VoiceConversation("link-5", ConversationState.Active);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-5", Arg.Any<CancellationToken>())
            .Returns(conversation);
        var agent = AgentInState(AgentState.Busy);
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(agent);
        var events = CaptureEvents();
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallEndedEvent("s-link-5", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(39)));

        conversation.State.Should().Be(ConversationState.WrapUp);
        await _capacity.Received(1).ReleaseAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), ChannelType.Voice, Arg.Any<CancellationToken>());
        agent.State.Should().Be(AgentState.ACW);
        await _agents.Received(1).SaveAsync(Arg.Is<Agent>(a => a.State == AgentState.ACW), Arg.Any<CancellationToken>());
        events.OfType<AgentStateChangedEvent>().Should().Contain(ev => ev.NewState == nameof(AgentState.ACW));
    }

    [Fact]
    public async Task HandleEventAsync_ShouldAbandonConversation_WhenEndedWhileStillQueued()
    {
        // Caller hung up before any agent answered — no agent interface on the session.
        var session = MakeSession("link-6", Tenant);
        _sessions.GetById("s-link-6").Returns(session);
        var conversation = VoiceConversation("link-6", ConversationState.Queued);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-6", Arg.Any<CancellationToken>())
            .Returns(conversation);
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallEndedEvent("s-link-6", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(8), null));

        conversation.State.Should().Be(ConversationState.Abandoned);
        await _capacity.DidNotReceive().ReleaseAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
        await _agents.DidNotReceive().SaveAsync(Arg.Any<Agent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleEventAsync_ShouldIgnore_WhenSessionIsOutbound()
    {
        var session = new CallSession("s-out", "link-out", "primary", CallDirection.Outbound);
        session.TenantId = Tenant;
        _sessions.GetById("s-out").Returns(session);
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallQueuedEvent("s-out", "primary", _clock.UtcNow, "acme-Support", 1));

        await _conversations.DidNotReceive().SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleEventAsync_ShouldReserveCapacityOnce_WhenConnectedRedelivered()
    {
        // The SDK emits ≥2 CallConnectedEvents per answered queue call; capacity must reserve once.
        var session = MakeSession("link-redeliver", Tenant, AgentInterface);
        _sessions.GetById("s-link-redeliver").Returns(session);
        var conversation = VoiceConversation("link-redeliver", ConversationState.Queued);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-redeliver", Arg.Any<CancellationToken>())
            .Returns(conversation);
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(_ => AgentInState(AgentState.Available));
        var bridge = CreateBridge();

        var connected = new CallConnectedEvent("s-link-redeliver", "primary", _clock.UtcNow, null, "acme-Support", TimeSpan.FromSeconds(2));
        await bridge.HandleEventAsync(connected);
        await bridge.HandleEventAsync(connected); // re-delivery: conversation is already Active

        conversation.State.Should().Be(ConversationState.Active);
        await _capacity.Received(1).ReserveAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), ChannelType.Voice, Arg.Any<CancellationToken>());
        await _conversations.Received(1).SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleEventAsync_ShouldRecoverTenantFromStoredConversation_WhenEndedSessionUnstamped()
    {
        // Failover: this pod became leader only at hangup, so session.TenantId is null and the trunk
        // channel is gone — the End handler must recover the tenant via the cross-tenant LinkedId lookup.
        var session = MakeSession("link-failover", tenant: null, AgentInterface);
        _sessions.GetById("s-link-failover").Returns(session);
        var conversation = VoiceConversation("link-failover", ConversationState.Active);
        _conversations.FindByVoiceLinkedIdAcrossTenantsAsync("link-failover", Arg.Any<CancellationToken>())
            .Returns(conversation);
        var agent = AgentInState(AgentState.Busy);
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(agent);
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallEndedEvent("s-link-failover", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(28)));

        conversation.State.Should().Be(ConversationState.WrapUp);
        await _capacity.Received(1).ReleaseAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), ChannelType.Voice, Arg.Any<CancellationToken>());
        agent.State.Should().Be(AgentState.ACW);
    }

    [Fact]
    public async Task HandleEventAsync_ShouldNotReserveOrSave_WhenConnectedButConversationMissing()
    {
        // A connect with no tracked Conversation (missed Queued) reserves nothing — capacity tracks
        // Conversations, and there is no row to advance.
        var session = MakeSession("link-noconv", Tenant, AgentInterface);
        _sessions.GetById("s-link-noconv").Returns(session);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-noconv", Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallConnectedEvent("s-link-noconv", "primary", _clock.UtcNow, null, "acme-Support", TimeSpan.FromSeconds(2)));

        await _conversations.DidNotReceive().SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
        await _capacity.DidNotReceive().ReserveAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
        await _agents.DidNotReceive().SaveAsync(Arg.Any<Agent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleEventAsync_ShouldStillActivateConversation_WhenAgentInterfaceMalformed()
    {
        // A whitespace-only agent-id suffix must not throw (per-step fault isolation) — the
        // conversation still advances to Active even though the agent step is skipped.
        var session = MakeSession("link-badiface", Tenant, $"PJSIP/{Tenant}-agent-   ");
        _sessions.GetById("s-link-badiface").Returns(session);
        var conversation = VoiceConversation("link-badiface", ConversationState.Queued);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-badiface", Arg.Any<CancellationToken>())
            .Returns(conversation);
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallConnectedEvent("s-link-badiface", "primary", _clock.UtcNow, null, "acme-Support", TimeSpan.FromSeconds(2)));

        conversation.State.Should().Be(ConversationState.Active);
        await _capacity.DidNotReceive().ReserveAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
    }

    // ─── ResolveTenantFromChannelAsync (the AMI GetVar wire — the tenant-resolution linchpin) ───

    private void StubGetVar(string? response, string? value)
    {
#pragma warning disable CA2012 // ValueTask in NSubstitute setup
        _ami.SendActionAsync<GetVarResponse>(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<GetVarResponse>(new GetVarResponse { Response = response, Value = value }));
#pragma warning restore CA2012
    }

    [Fact]
    public async Task ResolveTenantFromChannelAsync_ShouldReturnAndStampTenant_WhenGetVarSucceeds()
    {
        AddPrimaryServer();
        StubGetVar("Success", "acme");
        var session = MakeSession("link-gv1", tenant: null);
        var bridge = CreateBridge();

        var result = await bridge.ResolveTenantFromChannelAsync(session, "PJSIP/t-2-abc");

        result.Should().Be("acme");
        session.TenantId.Should().Be("acme");
    }

    [Fact]
    public async Task ResolveTenantFromChannelAsync_ShouldFailClosed_WhenVarUnset()
    {
        AddPrimaryServer();
        StubGetVar("Success", "");
        var session = MakeSession("link-gv2", tenant: null);
        var bridge = CreateBridge();

        var result = await bridge.ResolveTenantFromChannelAsync(session, "PJSIP/t-2-abc");

        result.Should().BeNull();
        session.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTenantFromChannelAsync_ShouldFailClosed_WhenResponseNotSuccess()
    {
        AddPrimaryServer();
        StubGetVar("Error", "acme");
        var session = MakeSession("link-gv3", tenant: null);
        var bridge = CreateBridge();

        var result = await bridge.ResolveTenantFromChannelAsync(session, "PJSIP/t-2-abc");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTenantFromChannelAsync_ShouldFailClosed_WhenAmiThrows()
    {
        AddPrimaryServer();
        _ami.SendActionAsync<GetVarResponse>(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("AMI down"));
        var session = MakeSession("link-gv4", tenant: null);
        var bridge = CreateBridge();

        var result = await bridge.ResolveTenantFromChannelAsync(session, "PJSIP/t-2-abc");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTenantFromChannelAsync_ShouldFailClosed_WhenNoPrimaryServer()
    {
        // Empty server pool — AMI not connected/configured.
        var session = MakeSession("link-gv5", tenant: null);
        var bridge = CreateBridge();

        var result = await bridge.ResolveTenantFromChannelAsync(session, "PJSIP/t-2-abc");

        result.Should().BeNull();
    }

    // ─── OnCallStarted outbound linkage (3B.2d.3 — lab-verified against the real SDK model) ───

    private void StubGetVarFor(string variable, string? value)
    {
#pragma warning disable CA2012 // ValueTask in NSubstitute setup
        _ami.SendActionAsync<GetVarResponse>(
                Arg.Is<ManagerAction>(a => a is GetVarAction && ((GetVarAction)a).Variable == variable),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<GetVarResponse>(new GetVarResponse
            {
                Response = string.IsNullOrEmpty(value) ? "Error" : "Success",
                Value = value,
            }));
#pragma warning restore CA2012
    }

    private Conversation OutboundConversation(EntityId agentId, string? voiceLinkedId = null) =>
        new()
        {
            ConversationId = EntityId.New(),
            TenantId = new TenantId(Tenant),
            ContactId = EntityId.New(),
            Channel = ChannelType.Voice,
            State = ConversationState.Active,
            Owner = new ConversationOwner(ConversationOwnerKind.Agent, agentId),
            CreatedAt = _clock.UtcNow,
            VoiceLinkedId = voiceLinkedId,
        };

    [Fact]
    public async Task LinkOutboundCallAsync_ShouldStampLinkedIdAndScreenPop_WhenOutboundVarPresent()
    {
        AddPrimaryServer();
        StubContact();
        var events = CaptureEvents();
        var agentId = EntityId.From(AgentGuid);
        var conv = OutboundConversation(agentId);
        _conversations.GetByIdAsync(new TenantId(Tenant), conv.ConversationId, Arg.Any<CancellationToken>()).Returns(conv);
        StubGetVarFor("VERBARA_OUTBOUND_ID", conv.ConversationId.Value);
        StubGetVarFor("TENANT_ID", Tenant);
        var session = new CallSession("s-out-link", "link-out-1", "primary", CallDirection.Inbound);

        var bridge = CreateBridge();
        await bridge.LinkOutboundCallAsync(session, "PJSIP/acme-agent-x");

        await _conversations.Received().SaveAsync(
            Arg.Is<Conversation>(c => c.ConversationId == conv.ConversationId && c.VoiceLinkedId == "link-out-1"),
            Arg.Any<CancellationToken>());
        events.OfType<VoiceScreenPopEvent>().Should().ContainSingle(ev =>
            ev.ConversationId == conv.ConversationId.Value
            && ev.AgentId == AgentGuid
            && ev.CorrelationId == conv.ConversationId.Value);
    }

    [Fact]
    public async Task LinkOutboundCallAsync_ShouldNoOp_WhenOutboundVarAbsent()
    {
        AddPrimaryServer();
        StubGetVarFor("VERBARA_OUTBOUND_ID", null); // not an outbound click-to-dial (e.g. inbound trunk leg)
        var session = new CallSession("s-in", "link-in", "primary", CallDirection.Inbound);

        var bridge = CreateBridge();
        await bridge.LinkOutboundCallAsync(session, "PJSIP/t-2-abc");

        await _conversations.DidNotReceive().GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>());
        await _conversations.DidNotReceive().SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkOutboundCallAsync_ShouldBeIdempotent_WhenAlreadyLinked()
    {
        AddPrimaryServer();
        var agentId = EntityId.From(AgentGuid);
        var conv = OutboundConversation(agentId, voiceLinkedId: "already-linked");
        _conversations.GetByIdAsync(new TenantId(Tenant), conv.ConversationId, Arg.Any<CancellationToken>()).Returns(conv);
        StubGetVarFor("VERBARA_OUTBOUND_ID", conv.ConversationId.Value);
        StubGetVarFor("TENANT_ID", Tenant);
        var session = new CallSession("s-out-redup", "link-redup", "primary", CallDirection.Inbound);

        var bridge = CreateBridge();
        await bridge.LinkOutboundCallAsync(session, "PJSIP/acme-agent-x");

        await _conversations.DidNotReceive().SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
    }

    // ─── IsAbnormalAgentHangup (W5b A1 — the pure callback-eval classifier) ───────

    [Fact]
    public void IsAbnormalAgentHangup_ShouldReturnTrue_WhenNonNormalCauseAndAgentLeftFirst()
    {
        var agentLeft = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
        var callerLeft = agentLeft.AddSeconds(2);

        VoiceConversationBridge.IsAbnormalAgentHangup(HangupCause.NormalTemporaryFailure, agentLeft, callerLeft)
            .Should().BeTrue();
    }

    [Fact]
    public void IsAbnormalAgentHangup_ShouldReturnTrue_WhenAgentLeftAndCallerStillPresent()
    {
        var agentLeft = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

        VoiceConversationBridge.IsAbnormalAgentHangup(HangupCause.NetworkOutOfOrder, agentLeft, callerLeftAt: null)
            .Should().BeTrue();
    }

    [Fact]
    public void IsAbnormalAgentHangup_ShouldReturnFalse_WhenNormalClearing()
    {
        var agentLeft = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

        VoiceConversationBridge.IsAbnormalAgentHangup(HangupCause.NormalClearing, agentLeft, callerLeftAt: null)
            .Should().BeFalse();
    }

    [Fact]
    public void IsAbnormalAgentHangup_ShouldReturnFalse_WhenCauseNull()
    {
        var agentLeft = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

        VoiceConversationBridge.IsAbnormalAgentHangup(agentCause: null, agentLeft, callerLeftAt: null)
            .Should().BeFalse();
    }

    [Fact]
    public void IsAbnormalAgentHangup_ShouldReturnFalse_WhenCallerHungUpFirst()
    {
        var callerLeft = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
        var agentLeft = callerLeft.AddSeconds(2);

        VoiceConversationBridge.IsAbnormalAgentHangup(HangupCause.NormalTemporaryFailure, agentLeft, callerLeft)
            .Should().BeFalse();
    }

    [Fact]
    public void IsAbnormalAgentHangup_ShouldReturnFalse_WhenAgentLeftAtNull()
    {
        var callerLeft = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

        VoiceConversationBridge.IsAbnormalAgentHangup(HangupCause.NormalTemporaryFailure, agentLeftAt: null, callerLeft)
            .Should().BeFalse();
    }

    // ─── OnCallEnded callback-eval fact stamping (W5b A1) ─────────────────────────

    private Queue StubQueue(string name)
    {
        var queue = new Queue
        {
            QueueId = EntityId.New(),
            TenantId = new TenantId(Tenant),
            Name = name,
            CreatedAt = _clock.UtcNow,
        };
        _queues.ListAsync(Arg.Any<TenantId>(), Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Queue>([queue], 1, 1, 500));
        return queue;
    }

    private Contact StubContactWithVoiceAddress(EntityId contactId, string voiceAddress)
    {
        var contact = new Contact
        {
            ContactId = contactId,
            TenantId = new TenantId(Tenant),
            CreatedAt = _clock.UtcNow,
        };
        contact.AddAddress(new ChannelAddress(ChannelType.Voice, voiceAddress));
        _contactStore.GetByIdAsync(Arg.Any<TenantId>(), contactId, Arg.Any<CancellationToken>())
            .Returns(contact);
        return contact;
    }

    [Fact]
    public async Task OnCallEnded_ShouldStampCallbackEvalFacts_WhenEndedAfterAnswer()
    {
        var session = MakeSession("link-cb1", Tenant, AgentInterface);
        session.QueueName = "acme-Support";
        _sessions.GetById("s-link-cb1").Returns(session);
        var conversation = VoiceConversation("link-cb1", ConversationState.Active);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-cb1", Arg.Any<CancellationToken>())
            .Returns(conversation);
        var queue = StubQueue("Support");
        StubContactWithVoiceAddress(conversation.ContactId, "+15551230000");
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(AgentInState(AgentState.Busy));
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallEndedEvent("s-link-cb1", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(39)));

        conversation.Metadata.Should().ContainKey("pendingCallbackEval").WhoseValue.Should().Be("true");
        conversation.Metadata.Should().ContainKey("callbackEvalSince")
            .WhoseValue.Should().Be(_clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        // No participants on the test session ⇒ null CallerIdNum ⇒ resolved from the contact's Voice address.
        conversation.Metadata.Should().ContainKey("callbackNumber").WhoseValue.Should().Be("+15551230000");
        conversation.Metadata.Should().ContainKey("originQueueId").WhoseValue.Should().Be(queue.QueueId.Value);
        // No participants ⇒ conservative default: agent leg NOT flagged abnormal.
        conversation.Metadata.Should().ContainKey("agentLegAbnormal").WhoseValue.Should().Be("false");
        // Existing behavior preserved: answered call → WrapUp + capacity released + ACW.
        conversation.State.Should().Be(ConversationState.WrapUp);
        await _capacity.Received(1).ReleaseAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), ChannelType.Voice, Arg.Any<CancellationToken>());
        await _agents.Received(1).SaveAsync(Arg.Is<Agent>(a => a.State == AgentState.ACW), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnCallEnded_ShouldNotStampCallbackEval_WhenEndedWhileQueued()
    {
        var session = MakeSession("link-cb2", Tenant);
        session.QueueName = "acme-Support";
        _sessions.GetById("s-link-cb2").Returns(session);
        var conversation = VoiceConversation("link-cb2", ConversationState.Queued);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-cb2", Arg.Any<CancellationToken>())
            .Returns(conversation);
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallEndedEvent("s-link-cb2", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(8), null));

        conversation.State.Should().Be(ConversationState.Abandoned);
        conversation.Metadata.Should().NotContainKey("pendingCallbackEval");
    }

    [Fact]
    public async Task OnCallEnded_ShouldNotStampCallbackNumber_WhenCallerAnonymous()
    {
        var session = MakeSession("link-cb3", Tenant, AgentInterface);
        session.QueueName = "acme-Support";
        _sessions.GetById("s-link-cb3").Returns(session);
        var conversation = VoiceConversation("link-cb3", ConversationState.Active);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-cb3", Arg.Any<CancellationToken>())
            .Returns(conversation);
        StubContactWithVoiceAddress(conversation.ContactId, "anonymous");
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(AgentInState(AgentState.Busy));
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallEndedEvent("s-link-cb3", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(39)));

        conversation.Metadata.Should().ContainKey("pendingCallbackEval").WhoseValue.Should().Be("true");
        conversation.Metadata.Should().NotContainKey("callbackNumber");
    }

    public void Dispose() => _eventBus.Dispose();
}
