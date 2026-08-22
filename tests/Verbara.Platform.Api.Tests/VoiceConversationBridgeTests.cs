using System.Globalization;
using System.Reactive.Subjects;
using System.Reflection;
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

    // CallSession.AddParticipant is SDK-internal (and this assembly is not in its InternalsVisibleTo
    // list), so a caller leg can only be attached via reflection. This is the one path the bridge uses
    // to find the trunk channel it reads VERBARA_REASON off, so the OnCallQueued capture tests need it.
    private static void AddCaller(CallSession session, string channel)
    {
        var participant = new SessionParticipant
        {
            UniqueId = channel,
            Channel = channel,
            Technology = "PJSIP",
            Role = ParticipantRole.Caller,
        };
        var add = typeof(CallSession).GetMethod("AddParticipant", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?? throw new InvalidOperationException("CallSession.AddParticipant not found.");
        add.Invoke(session, [participant]);
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
            Arg.Is<Conversation>(c => c != null &&
                c.Channel == ChannelType.Voice &&
                c.TenantId.Value == Tenant &&
                c.VoiceLinkedId == "link-1" &&
                c.ContactId == contact.ContactId &&
                c.State == ConversationState.Queued),
            Arg.Any<CancellationToken>());
        // No participant on the test session ⇒ null CallerIdNum ⇒ documented 'anonymous' voice contact.
        await _contacts.Received(1).ResolveAsync(
            Arg.Is<TenantId>(t => t.Value == Tenant),
            Arg.Is<ChannelAddress>(a => a != null && a.Channel == ChannelType.Voice && a.Address == "anonymous"),
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
        await _agents.Received(1).SaveAsync(Arg.Is<Agent>(a => a != null && a.State == AgentState.Busy), Arg.Any<CancellationToken>());
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
        await _agents.Received(1).SaveAsync(Arg.Is<Agent>(a => a != null && a.State == AgentState.ACW), Arg.Any<CancellationToken>());
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

    // ─── ResolveReasonFromChannelAsync (Typification P1 implicit voice capture — VERBARA_REASON) ───

    [Fact]
    public async Task ResolveReasonFromChannelAsync_ShouldReturnValue_WhenAmiGetVarSucceeds()
    {
        AddPrimaryServer();
        const string ReasonPath = "[\"CITAS\",\"REPROG\"]";
        StubGetVarFor("VERBARA_REASON", ReasonPath);
        var bridge = CreateBridge();

        var result = await bridge.ResolveReasonFromChannelAsync("PJSIP/t-2-abc", CancellationToken.None);

        result.Should().Be(ReasonPath);
    }

    [Fact]
    public async Task ResolveReasonFromChannelAsync_ShouldReturnNull_WhenAmiUnavailable()
    {
        // Empty server pool — AMI not connected/configured ⇒ no reason to capture.
        var bridge = CreateBridge();

        var result = await bridge.ResolveReasonFromChannelAsync("PJSIP/t-2-abc", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task OnCallQueued_ShouldStampReasonPathMetadata_WhenChannelVarPresent()
    {
        AddPrimaryServer();
        const string ReasonPath = "[\"CITAS\",\"REPROG\"]";
        StubGetVarFor("VERBARA_REASON", ReasonPath);
        StubContact();
        // A caller leg is required so the bridge can read VERBARA_REASON off the trunk channel.
        var session = MakeSession("link-reason", Tenant);
        AddCaller(session, "PJSIP/acme-trunk/+15551230000");
        _sessions.GetById("s-link-reason").Returns(session);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-reason", Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallQueuedEvent("s-link-reason", "primary", _clock.UtcNow, "acme-Support", 1));

        // The reason taxonomy path is stamped onto the freshly-created Conversation, in the SAME save.
        await _conversations.Received(1).SaveAsync(
            Arg.Is<Conversation>(c => c != null &&
                c.VoiceLinkedId == "link-reason" &&
                c.Metadata.ContainsKey("reasonPath") &&
                c.Metadata["reasonPath"] == ReasonPath),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnCallQueued_ShouldNotStampReasonPath_WhenChannelVarEmpty()
    {
        AddPrimaryServer();
        StubGetVarFor("VERBARA_REASON", null); // var unset ⇒ GetVar "Error" ⇒ no reason captured
        StubContact();
        var session = MakeSession("link-noreason", Tenant);
        AddCaller(session, "PJSIP/acme-trunk/+15551230000");
        _sessions.GetById("s-link-noreason").Returns(session);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-noreason", Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallQueuedEvent("s-link-noreason", "primary", _clock.UtcNow, "acme-Support", 1));

        // Conversation still created (voice tracking never depends on a reason), but no reasonPath stamped.
        await _conversations.Received(1).SaveAsync(
            Arg.Is<Conversation>(c => c != null && c.VoiceLinkedId == "link-noreason" && !c.Metadata.ContainsKey("reasonPath")),
            Arg.Any<CancellationToken>());
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
            Arg.Is<Conversation>(c => c != null && c.ConversationId == conv.ConversationId && c.VoiceLinkedId == "link-out-1"),
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

    // ─── W5b callback-rescue conversation lifecycle through the bridge (A5) ───────
    //
    // CallbackOriginator (A4) pre-creates a tracked rescue Conversation (Voice, Queued, Owner=null,
    // metadata direction="callback-rescue" + rescuedFrom + callbackAttempts) and AMI-Originates the
    // dropped customer into [stasis-queue] with VERBARA_OUTBOUND_ID = that Conversation id. The SDK
    // classifies the callback as Inbound (stasis-queue is not an outbound pattern), so the existing
    // Inbound-gated handlers process it. These tests LOCK IN that the unchanged bridge:
    //   1. links the Owner=null rescue conv via LinkOutboundCallAsync WITHOUT a screen-pop/capacity
    //      reserve (no owning agent yet) and preserves its rescue metadata;
    //   2. does NOT create a duplicate on the following CallQueued (already linked);
    //   3. activates it normally on answer (Active + owner + screen-pop + capacity + Busy) while the
    //      rescuedFrom / callbackAttempts metadata SURVIVES — so a re-dropped callback chains correctly.

    private Conversation RescueConversation(string rescuedFrom, string callbackAttempts, string? voiceLinkedId = null)
    {
        var conv = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = new TenantId(Tenant),
            ContactId = EntityId.New(),
            Channel = ChannelType.Voice,
            State = ConversationState.Queued,
            Owner = null,
            QueuePriority = -1,
            CreatedAt = _clock.UtcNow,
            VoiceLinkedId = voiceLinkedId,
        };
        conv.SetMetadata("direction", "callback-rescue");
        conv.SetMetadata("rescuedFrom", rescuedFrom);
        conv.SetMetadata("callbackAttempts", callbackAttempts);
        return conv;
    }

    [Fact]
    public async Task LinkOutboundCallAsync_ShouldLinkRescueConversationWithoutScreenPop_WhenOwnerNull()
    {
        AddPrimaryServer();
        var events = CaptureEvents();
        var rescuedFrom = EntityId.New().Value;
        var conv = RescueConversation(rescuedFrom, callbackAttempts: "1");
        _conversations.GetByIdAsync(new TenantId(Tenant), conv.ConversationId, Arg.Any<CancellationToken>()).Returns(conv);
        StubGetVarFor("VERBARA_OUTBOUND_ID", conv.ConversationId.Value);
        StubGetVarFor("TENANT_ID", Tenant);
        var session = new CallSession("s-rescue-link", "link-rescue-1", "primary", CallDirection.Inbound);

        var bridge = CreateBridge();
        await bridge.LinkOutboundCallAsync(session, "PJSIP/acme-trunk/+15551230000");

        // Linked + saved with the call's LinkedId.
        await _conversations.Received().SaveAsync(
            Arg.Is<Conversation>(c => c != null && c.ConversationId == conv.ConversationId && c.VoiceLinkedId == "link-rescue-1"),
            Arg.Any<CancellationToken>());
        // Owner=null ⇒ the agent screen-pop / capacity-reserve block is intentionally skipped.
        events.OfType<VoiceScreenPopEvent>().Should().BeEmpty();
        await _capacity.DidNotReceive().ReserveAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
        // Rescue metadata survives the link untouched.
        conv.Metadata.Should().ContainKey("rescuedFrom").WhoseValue.Should().Be(rescuedFrom);
        conv.Metadata.Should().ContainKey("direction").WhoseValue.Should().Be("callback-rescue");
    }

    [Fact]
    public async Task OnCallQueued_ShouldNotCreateDuplicate_WhenRescueConversationAlreadyLinked()
    {
        // The preceding CallStarted/LinkOutboundCallAsync already stamped VoiceLinkedId on the rescue
        // conv, so the by-LinkedId lookup finds it here and the create is skipped (no duplicate).
        var session = MakeSession("link-rescue-q", Tenant);
        _sessions.GetById("s-link-rescue-q").Returns(session);
        // Build the rescue conv BEFORE the Returns() call: it reads _clock.UtcNow (a substitute), which
        // NSubstitute would otherwise treat as configuring a call inside the Returns() argument.
        var existing = RescueConversation(EntityId.New().Value, "1", voiceLinkedId: "link-rescue-q");
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-rescue-q", Arg.Any<CancellationToken>())
            .Returns(existing);
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallQueuedEvent("s-link-rescue-q", "primary", _clock.UtcNow, "acme-Support", 10));

        await _conversations.DidNotReceive().SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
        await _contacts.DidNotReceive().ResolveAsync(Arg.Any<TenantId>(), Arg.Any<ChannelAddress>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnCallConnected_ShouldActivateRescueConversationPreservingMetadata_WhenAnswered()
    {
        var session = MakeSession("link-rescue-conn", Tenant, AgentInterface);
        _sessions.GetById("s-link-rescue-conn").Returns(session);
        var rescuedFrom = EntityId.New().Value;
        var conversation = RescueConversation(rescuedFrom, callbackAttempts: "1", voiceLinkedId: "link-rescue-conn");
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-rescue-conn", Arg.Any<CancellationToken>())
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
            CreatedAt = _clock.UtcNow,
        };
        _contactStore.GetByIdAsync(Arg.Any<TenantId>(), conversation.ContactId, Arg.Any<CancellationToken>())
            .Returns(screenPopContact);
        var events = CaptureEvents();
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallConnectedEvent("s-link-rescue-conn", "primary", _clock.UtcNow, null, "acme-Support", TimeSpan.FromSeconds(3)));

        // Activated + owner assigned exactly like a normal inbound queue call.
        conversation.State.Should().Be(ConversationState.Active);
        conversation.Owner.Should().NotBeNull();
        conversation.Owner!.Kind.Should().Be(ConversationOwnerKind.Agent);
        conversation.Owner!.OwnerId!.Value.Value.Should().Be(AgentGuid);
        await _capacity.Received(1).ReserveAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), ChannelType.Voice, Arg.Any<CancellationToken>());
        agent.State.Should().Be(AgentState.Busy);
        events.OfType<VoiceScreenPopEvent>().Should().ContainSingle(ev =>
            ev.AgentId == AgentGuid && ev.ConversationId == conversation.ConversationId.Value);
        // The anti-loop counter + provenance SURVIVE activation so a re-dropped callback chains correctly.
        conversation.Metadata.Should().ContainKey("rescuedFrom").WhoseValue.Should().Be(rescuedFrom);
        conversation.Metadata.Should().ContainKey("callbackAttempts").WhoseValue.Should().Be("1");
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
        await _agents.Received(1).SaveAsync(Arg.Is<Agent>(a => a != null && a.State == AgentState.ACW), Arg.Any<CancellationToken>());
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

    // ─── csat-completion — VoiceAgentHangupEvent publish (Platform/ADR-0020) ─────

    // Adds a participant carrying a HangupCause + LeftAt (SDK-internal AddParticipant, via reflection —
    // mirrors AddCaller) so the abnormal-hangup verdict can be exercised end-to-end through the bridge.
    private static void AddParticipantWithHangup(
        CallSession session, string channel, ParticipantRole role, HangupCause? cause, DateTimeOffset? leftAt)
    {
        var participant = new SessionParticipant
        {
            UniqueId = channel,
            Channel = channel,
            Technology = "PJSIP",
            Role = role,
            HangupCause = cause,
            LeftAt = leftAt,
        };
        var add = typeof(CallSession).GetMethod("AddParticipant", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?? throw new InvalidOperationException("CallSession.AddParticipant not found.");
        add.Invoke(session, [participant]);
    }

    [Fact]
    public async Task OnCallEnded_ShouldPublishNonAbnormalHangupEvent_WhenCleanHangupAfterAnswer()
    {
        var session = MakeSession("link-hg-clean", Tenant, AgentInterface);
        session.QueueName = "acme-Support";
        _sessions.GetById("s-link-hg-clean").Returns(session);
        // Agent leg ends cleanly (NormalClearing) → IsAbnormalAgentHangup returns false.
        AddParticipantWithHangup(session, "PJSIP/acme-agent-1", ParticipantRole.Agent,
            HangupCause.NormalClearing, _clock.UtcNow);
        var conversation = VoiceConversation("link-hg-clean", ConversationState.Active);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-hg-clean", Arg.Any<CancellationToken>())
            .Returns(conversation);
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(AgentInState(AgentState.Busy));
        var events = CaptureEvents();
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallEndedEvent("s-link-hg-clean", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(39)));

        events.OfType<VoiceAgentHangupEvent>().Should().ContainSingle(ev =>
            ev.TenantId == Tenant &&
            ev.ConversationId == conversation.ConversationId.Value &&
            ev.QueueName == "Support" &&
            ev.Abnormal == false);
    }

    [Fact]
    public async Task OnCallEnded_ShouldPublishAbnormalHangupEvent_WhenAgentLegDiesAbnormally()
    {
        var session = MakeSession("link-hg-abn", Tenant, AgentInterface);
        session.QueueName = "acme-Support";
        _sessions.GetById("s-link-hg-abn").Returns(session);
        // Agent leg dies with a non-normal cause and leaves before the caller → abnormal verdict is true.
        var agentLeft = _clock.UtcNow;
        AddParticipantWithHangup(session, "PJSIP/acme-agent-1", ParticipantRole.Agent,
            HangupCause.NetworkOutOfOrder, agentLeft);
        AddParticipantWithHangup(session, "PJSIP/acme-trunk/+15551230000", ParticipantRole.Caller,
            HangupCause.NormalClearing, agentLeft.AddSeconds(2));
        var conversation = VoiceConversation("link-hg-abn", ConversationState.Active);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-hg-abn", Arg.Any<CancellationToken>())
            .Returns(conversation);
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(AgentInState(AgentState.Busy));
        var events = CaptureEvents();
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallEndedEvent("s-link-hg-abn", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(39)));

        events.OfType<VoiceAgentHangupEvent>().Should().ContainSingle(ev =>
            ev.ConversationId == conversation.ConversationId.Value && ev.Abnormal);
    }

    [Fact]
    public async Task OnCallEnded_ShouldNotPublishHangupEvent_WhenFollowerPod()
    {
        // A follower pod (no AMI-owner lease) produces no side-effects, so nothing is published.
        var session = MakeSession("link-hg-follower", Tenant, AgentInterface);
        _sessions.GetById("s-link-hg-follower").Returns(session);
        var events = CaptureEvents();
        var bridge = CreateBridge(isLeader: false);

        await bridge.HandleEventAsync(new CallEndedEvent("s-link-hg-follower", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(39)));

        events.OfType<VoiceAgentHangupEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task OnCallEnded_ShouldNotPublishHangupEvent_WhenNeverAnsweredAbandoned()
    {
        // A never-answered call transitions Queued → Abandoned; it carries no served interaction and no
        // abnormal verdict, so no VoiceAgentHangupEvent is published.
        var session = MakeSession("link-hg-aband", Tenant);
        session.QueueName = "acme-Support";
        _sessions.GetById("s-link-hg-aband").Returns(session);
        var conversation = VoiceConversation("link-hg-aband", ConversationState.Queued);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-hg-aband", Arg.Any<CancellationToken>())
            .Returns(conversation);
        var events = CaptureEvents();
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallEndedEvent("s-link-hg-aband", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(8), null));

        conversation.State.Should().Be(ConversationState.Abandoned);
        events.OfType<VoiceAgentHangupEvent>().Should().BeEmpty();
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
