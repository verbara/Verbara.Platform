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
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Api.Tests.Logging;
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

    private VoiceConversationBridge CreateBridge(bool isLeader = true, IAgentCapacityService? capacity = null) =>
        new(_sessions, _serverPool, _conversations, _contacts, _contactStore, _agents, _queues, capacity ?? _capacity, _eventBus,
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

    [Fact]
    public async Task HandleEventAsync_ShouldSetNoOwnerAndNoBusy_WhenTheConnectedInterfaceIsAListenEndpoint()
    {
        // supervisor-silent-listen 2.8 (a), design D14: a supervisor listen endpoint is PJSIP/{t}-agent-sup-{userId};
        // read as an agent it would become the owner (the connect sets the owner with no existence check).
        var session = MakeSession("link-listen", Tenant, $"PJSIP/{Tenant}-agent-sup-7d2c9e4b1a5f4c3e8b6d0f1a2c3e4b5d");
        _sessions.GetById("s-link-listen").Returns(session);
        var conversation = VoiceConversation("link-listen", ConversationState.Queued);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-listen", Arg.Any<CancellationToken>())
            .Returns(conversation);
        var events = CaptureEvents();
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallConnectedEvent("s-link-listen", "primary", _clock.UtcNow, null, "acme-Support", TimeSpan.FromSeconds(2)));

        conversation.Owner.Should().BeNull("a listen slot is never an agent");
        await _capacity.DidNotReceive().ReserveAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
        await _agents.DidNotReceive().SaveAsync(Arg.Any<Agent>(), Arg.Any<CancellationToken>());
        events.OfType<AgentStateChangedEvent>().Should().BeEmpty();
        events.OfType<VoiceScreenPopEvent>().Should().BeEmpty();
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
    public async Task ResolveTenantFromChannelAsync_ShouldFailClosed_WhenTheNewSessionAnswersWithAnError()
    {
        // After a reconnect a read for a channel of the lost session goes out on the new AMI session, which answers
        // with an error. The Value is set on purpose: the Response alone must close it. The stubs below let a wrongly
        // resolved tenant reach the save, so the conversation assertion can fail.
        const string channel = "PJSIP/t-2-abc";
        AddPrimaryServer();
        StubGetVar("Error", "acme");
        StubContact();
        var session = MakeSession("link-gv6", tenant: null);
        AddCaller(session, channel);
        _sessions.GetById("s-link-gv6").Returns(session);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-gv6", Arg.Any<CancellationToken>())
            .Returns(default(Conversation?));
        var bridge = CreateBridge();

        var result = await bridge.ResolveTenantFromChannelAsync(session, channel);
        await bridge.HandleEventAsync(new CallQueuedEvent("s-link-gv6", "primary", _clock.UtcNow, "acme-Support", 1));

        using var scope = new AssertionScope();
        result.Should().BeNull();
        session.TenantId.Should().BeNull("an error answer stamps no tenant");
        _conversations.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IConversationStore.SaveAsync))
            .Should().BeEmpty("an unresolved tenant creates no conversation");
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
        CallSession session, string channel, ParticipantRole role, HangupCause? cause, DateTimeOffset? leftAt,
        DateTimeOffset? joinedAt = null)
    {
        var participant = new SessionParticipant
        {
            UniqueId = channel,
            Channel = channel,
            Technology = "PJSIP",
            Role = role,
            HangupCause = cause,
            LeftAt = leftAt,
            // Deterministic: JoinedAt otherwise defaults to the wall clock, after every fake-clock instant.
            JoinedAt = joinedAt ?? DateTimeOffset.UnixEpoch,
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
        // Agent leg ends cleanly (NormalClearing) → IsAbnormalAgentHangup returns false. Roles are the ones the
        // SDK's InferRole assigns: the customer leg comes first (Caller), the queue member second (Destination).
        AddParticipantWithHangup(session, "PJSIP/acme-trunk-00000001", ParticipantRole.Caller,
            HangupCause.NormalClearing, _clock.UtcNow);
        AddParticipantWithHangup(session, $"{AgentInterface}-00000002", ParticipantRole.Destination,
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
        AddParticipantWithHangup(session, "PJSIP/acme-trunk-00000001", ParticipantRole.Caller,
            HangupCause.NormalClearing, agentLeft.AddSeconds(2));
        AddParticipantWithHangup(session, $"{AgentInterface}-00000002", ParticipantRole.Destination,
            HangupCause.NetworkOutOfOrder, agentLeft);
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

    // ─── #384: the stamp finds the agent leg by channel, never by ParticipantRole.Agent ───────────────
    // The SDK's InferRole only assigns Internal (Local/), Caller (first leg) and Destination (every later
    // leg). These fixtures use exactly those roles and real channel names, as cell g of PLT-DIAL-02 saw them.

    private async Task<(Conversation Conversation, List<PlatformEvent> Events)> EndAnsweredCallAsync(
        string linkedId, CallSession session, ConversationOwner? owner = null)
    {
        _sessions.GetById($"s-{linkedId}").Returns(session);
        var conversation = VoiceConversation(linkedId, ConversationState.Active);
        if (owner is not null)
            conversation.Owner = owner;
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), linkedId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(AgentInState(AgentState.Busy));
        var events = CaptureEvents();
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallEndedEvent($"s-{linkedId}", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(39)));
        return (conversation, events);
    }

    [Fact]
    public async Task OnCallEnded_ShouldStampAgentLegAbnormalTrue_WhenQueueMemberLegDropsWithNetworkOutOfOrder()
    {
        var session = MakeSession("link-384-q", Tenant, AgentInterface);
        session.ConnectedAt = _clock.UtcNow.AddSeconds(-30);
        session.QueueName = "acme-Support";
        var dropped = _clock.UtcNow;
        AddParticipantWithHangup(session, "PJSIP/t-1-000000b4", ParticipantRole.Caller,
            HangupCause.NetworkOutOfOrder, dropped);
        AddParticipantWithHangup(session, $"{AgentInterface}-000000b5", ParticipantRole.Destination,
            HangupCause.NetworkOutOfOrder, dropped);

        var (conversation, events) = await EndAnsweredCallAsync("link-384-q", session);

        conversation.Metadata.Should().ContainKey("agentLegAbnormal").WhoseValue.Should().Be("true");
        events.OfType<VoiceAgentHangupEvent>().Should().ContainSingle(ev => ev.Abnormal);
    }

    [Fact]
    public async Task OnCallEnded_ShouldStampAgentLegAbnormalTrue_WhenOutboundAgentLegIsTheFirstChannel()
    {
        // Click-to-dial / rescued callback: the agent's leg is created first (Caller), the customer second
        // (Destination), and there is no member interface — the agent comes from the conversation owner.
        var session = MakeSession("link-384-out", Tenant);
        var dropped = _clock.UtcNow;
        AddParticipantWithHangup(session, $"{AgentInterface}-000000c1", ParticipantRole.Caller,
            HangupCause.NetworkOutOfOrder, dropped);
        AddParticipantWithHangup(session, "PJSIP/t-1-000000c2", ParticipantRole.Destination,
            HangupCause.NetworkOutOfOrder, dropped.AddMilliseconds(1));

        var (conversation, _) = await EndAnsweredCallAsync("link-384-out", session,
            new ConversationOwner(ConversationOwnerKind.Agent, EntityId.From(AgentGuid)));

        conversation.Metadata.Should().ContainKey("agentLegAbnormal").WhoseValue.Should().Be("true");
    }

    [Fact]
    public async Task OnCallEnded_ShouldStampAgentLegAbnormalFalse_WhenOutboundCustomerHungUpFirst()
    {
        // Same outbound shape, but the customer (Destination) leaves first: a customer-initiated end. A lookup
        // that took the first leg as "the caller" would compare the agent with itself and call this abnormal.
        var session = MakeSession("link-384-out-cust", Tenant);
        var customerLeft = _clock.UtcNow;
        AddParticipantWithHangup(session, $"{AgentInterface}-000000d1", ParticipantRole.Caller,
            HangupCause.NetworkOutOfOrder, customerLeft.AddSeconds(1));
        AddParticipantWithHangup(session, "PJSIP/t-1-000000d2", ParticipantRole.Destination,
            HangupCause.NormalClearing, customerLeft);

        var (conversation, _) = await EndAnsweredCallAsync("link-384-out-cust", session,
            new ConversationOwner(ConversationOwnerKind.Agent, EntityId.From(AgentGuid)));

        conversation.Metadata.Should().ContainKey("agentLegAbnormal").WhoseValue.Should().Be("false");
    }

    [Fact]
    public async Task OnCallEnded_ShouldStampAgentLegAbnormalFalse_WhenAnotherAgentsLegDropped()
    {
        // Only a leg of THIS call's agent counts: a leg whose channel names another agent is never the agent leg.
        var session = MakeSession("link-384-other", Tenant, AgentInterface);
        session.ConnectedAt = _clock.UtcNow.AddSeconds(-30);
        var dropped = _clock.UtcNow;
        AddParticipantWithHangup(session, "PJSIP/t-1-000000e1", ParticipantRole.Caller,
            HangupCause.NormalClearing, dropped.AddSeconds(1));
        AddParticipantWithHangup(session, $"PJSIP/{Tenant}-agent-22222222222222222222222222222222-000000e2",
            ParticipantRole.Destination, HangupCause.NetworkOutOfOrder, dropped);

        var (conversation, _) = await EndAnsweredCallAsync("link-384-other", session);

        conversation.Metadata.Should().ContainKey("agentLegAbnormal").WhoseValue.Should().Be("false");
    }

    [Fact]
    public async Task OnCallEnded_ShouldStampAgentLegAbnormalFalse_WhenReloadEndedTheCall()
    {
        // ADR-0062: a reload-produced ending carries no cause on any leg. sdk-pin-cascade requires false here.
        var session = MakeSession("link-384-reload", Tenant, AgentInterface);
        session.ConnectedAt = _clock.UtcNow.AddSeconds(-30);
        var gone = _clock.UtcNow;
        AddParticipantWithHangup(session, "PJSIP/t-1-000000f1", ParticipantRole.Caller, cause: null, gone);
        AddParticipantWithHangup(session, $"{AgentInterface}-000000f2", ParticipantRole.Destination, cause: null, gone);

        var (conversation, _) = await EndAnsweredCallAsync("link-384-reload", session);

        conversation.Metadata.Should().ContainKey("agentLegAbnormal").WhoseValue.Should().Be("false");
    }

    [Fact]
    public async Task OnCallEnded_ShouldStampAgentLegAbnormalTrue_WhenALocalLegLeftBeforeTheAgent()
    {
        // A Local/ helper channel (Internal) is never the customer, even when it is the first leg of the call (an
        // originate through a Local/ channel): its earlier departure must not read as "the caller hung up first".
        var session = MakeSession("link-384-local", Tenant, AgentInterface);
        session.ConnectedAt = _clock.UtcNow.AddSeconds(-30);
        var dropped = _clock.UtcNow;
        AddParticipantWithHangup(session, "Local/100@from-queue-00000001;1", ParticipantRole.Internal,
            HangupCause.NormalClearing, dropped.AddSeconds(-1));
        AddParticipantWithHangup(session, "PJSIP/t-1-00000101", ParticipantRole.Destination,
            HangupCause.NetworkOutOfOrder, dropped);
        AddParticipantWithHangup(session, $"{AgentInterface}-00000102", ParticipantRole.Destination,
            HangupCause.NetworkOutOfOrder, dropped);

        var (conversation, _) = await EndAnsweredCallAsync("link-384-local", session);

        conversation.Metadata.Should().ContainKey("agentLegAbnormal").WhoseValue.Should().Be("true");
    }

    [Fact]
    public async Task OnCallEnded_ShouldStampAgentLegAbnormalFalse_WhenTheAgentWasRungAgainAndDidNotAnswer()
    {
        // The queue rang the agent once without an answer, then again, and the agent took the call. Later they
        // blind-transferred it back to a queue they are a member of, were rung again and did not answer (another
        // member did). Neither unanswered ring is the leg that took the call: the first left before the connect,
        // the last joined after it, so their non-normal causes must not flag the call abnormal.
        var session = MakeSession("link-384-rering", Tenant, AgentInterface);
        session.QueueName = "acme-Support";
        var connected = _clock.UtcNow.AddMinutes(-5);
        session.ConnectedAt = connected;
        AddParticipantWithHangup(session, "PJSIP/t-1-00000201", ParticipantRole.Caller,
            HangupCause.NormalClearing, connected.AddSeconds(200), joinedAt: connected.AddSeconds(-60));
        AddParticipantWithHangup(session, $"{AgentInterface}-00000200", ParticipantRole.Destination,
            HangupCause.NoAnswer, connected.AddSeconds(-25), joinedAt: connected.AddSeconds(-40));
        AddParticipantWithHangup(session, $"{AgentInterface}-00000202", ParticipantRole.Destination,
            HangupCause.NormalClearing, connected.AddSeconds(60), joinedAt: connected.AddSeconds(-5));
        AddParticipantWithHangup(session, $"{AgentInterface}-00000203", ParticipantRole.Destination,
            HangupCause.NoAnswer, connected.AddSeconds(75), joinedAt: connected.AddSeconds(70));

        var (conversation, _) = await EndAnsweredCallAsync("link-384-rering", session);

        conversation.Metadata.Should().ContainKey("agentLegAbnormal").WhoseValue.Should().Be("false");
    }

    [Fact]
    public async Task OnCallEnded_ShouldStampAgentLegAbnormalFalse_WhenTheCustomerLegLeftFirstWithTheSameCause()
    {
        // Both legs report the same cause (as cell g saw), so only the order the SDK processed the two Hangups
        // tells an agent drop from a customer drop: the customer's leg leaving first is a customer-initiated end.
        var session = MakeSession("link-384-custfirst", Tenant, AgentInterface);
        session.ConnectedAt = _clock.UtcNow.AddSeconds(-30);
        var dropped = _clock.UtcNow;
        AddParticipantWithHangup(session, "PJSIP/t-1-00000301", ParticipantRole.Caller,
            HangupCause.NetworkOutOfOrder, dropped);
        AddParticipantWithHangup(session, $"{AgentInterface}-00000302", ParticipantRole.Destination,
            HangupCause.NetworkOutOfOrder, dropped.AddMilliseconds(1));

        var (conversation, _) = await EndAnsweredCallAsync("link-384-custfirst", session);

        conversation.Metadata.Should().ContainKey("agentLegAbnormal").WhoseValue.Should().Be("false");
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

    // ─── Campaign calls (dialer-places-calls-end-to-end, design D10) ─────────────────────────────
    // An answered campaign call that reaches an agent queue is classified Inbound by the Sdk, so it takes the
    // queue path. Pro's builder sets DIALER_CAMPAIGN_ID and DIALER_NUMBER on the originated trunk leg; the
    // session's caller id is the campaign's own outbound number (copied once at Newchannel), never the customer's.

    private const string CampaignTrunkLeg = "PJSIP/t-7-0000c001";
    private const string CampaignAgentLeg = "PJSIP/acme-agent-11111111111111111111111111111111-0000c002";
    private const string DialedNumber = "+573001234567";
    private const string CampaignCallerId = "+15550100";

    private VoiceConversationBridge CreateBridge(ILogger<VoiceConversationBridge> logger) =>
        new(_sessions, _serverPool, _conversations, _contacts, _contactStore, _agents, _queues, _capacity, _eventBus,
            LeaderStub(true), _clock, logger);

    // SDK-internal AddParticipant via reflection, as AddCaller does, with the leg's caller id.
    private static void AddLeg(
        CallSession session, string channel, ParticipantRole role, string? callerIdNum,
        HangupCause? cause = null, DateTimeOffset? leftAt = null)
    {
        var participant = new SessionParticipant
        {
            UniqueId = channel,
            Channel = channel,
            Technology = "PJSIP",
            Role = role,
            CallerIdNum = callerIdNum,
            HangupCause = cause,
            LeftAt = leftAt,
            JoinedAt = DateTimeOffset.UnixEpoch,
        };
        var add = typeof(CallSession).GetMethod("AddParticipant", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?? throw new InvalidOperationException("CallSession.AddParticipant not found.");
        add.Invoke(session, [participant]);
    }

    /// <summary>
    /// A session whose first (trunk) leg carries <paramref name="callerIdNum"/>, with no pre-stamped tenant: the
    /// bridge reads <c>TENANT_ID</c> off the leg.
    /// </summary>
    private CallSession TrunkSession(string linkedId, string? callerIdNum)
    {
        var session = MakeSession(linkedId, tenant: null, AgentInterface);
        AddLeg(session, CampaignTrunkLeg, ParticipantRole.Caller, callerIdNum);
        _sessions.GetById($"s-{linkedId}").Returns(session);
        return session;
    }

    /// <summary>
    /// The leg's channel variables: <c>TENANT_ID</c> = acme, no <c>VERBARA_OUTBOUND_ID</c> or <c>VERBARA_REASON</c>,
    /// and the two dialer variables (null = the GetVar returns <c>Error</c>).
    /// </summary>
    private void StubLegVariables(
        string? campaignId, string? dialedNumber,
        string? dialerContactId = DialerContactId, string? dialerActionId = DialerActionId)
    {
        AddPrimaryServer();
        StubGetVarFor("TENANT_ID", Tenant);
        StubGetVarFor("VERBARA_OUTBOUND_ID", null);
        StubGetVarFor("VERBARA_REASON", null);
        StubGetVarFor("DIALER_CAMPAIGN_ID", campaignId);
        StubGetVarFor("DIALER_NUMBER", dialedNumber);
        StubGetVarFor("DIALER_CONTACT_ID", campaignId is null ? null : dialerContactId);
        StubGetVarFor("DIALER_ACTION_ID", campaignId is null ? null : dialerActionId);
    }

    private const string DialerContactId = "4711";
    private const string DialerActionId = "5f0c2d9e8a7b4c3d9e1f0a2b3c4d5e6f";

    // dialer-honours-dnc-callbacks-and-agent-dispositions, design D4: the campaign Conversation carries the dialer's
    // contact id and the originate's action id, read with DIALER_CAMPAIGN_ID, so a typification can reach the dialer.
    [Fact]
    public async Task HandleEventAsync_ShouldStampTheDialerContactAndActionIds_WhenTheLegCarriesThem()
    {
        StubLegVariables(campaignId: "11", dialedNumber: DialedNumber);
        TrunkSession("link-camp-ids-1", CampaignCallerId);
        StubContactsFor(DialedNumber, CampaignCallerId, "anonymous");
        var saved = TrackConversations("link-camp-ids-1");
        var bridge = CreateBridge();

        await StartAndQueueAsync(bridge, "link-camp-ids-1");

        saved.Should().ContainSingle();
        saved[0].Metadata.Should().ContainKey("campaignId").WhoseValue.Should().Be("11");
        saved[0].Metadata.Should().ContainKey("dialerContactId").WhoseValue.Should().Be(DialerContactId);
        saved[0].Metadata.Should().ContainKey("dialerActionId").WhoseValue.Should().Be(DialerActionId);
    }

    [Theory]
    [InlineData(null, DialerActionId)]
    [InlineData(DialerContactId, null)]
    public async Task HandleEventAsync_ShouldStampOnlyTheCampaignAndWarn_WhenADialerIdCannotBeRead(
        string? dialerContactId, string? dialerActionId)
    {
        StubLegVariables(campaignId: "11", dialedNumber: DialedNumber, dialerContactId, dialerActionId);
        TrunkSession("link-camp-ids-2", CampaignCallerId);
        StubContactsFor(DialedNumber, CampaignCallerId, "anonymous");
        var saved = TrackConversations("link-camp-ids-2");
        var logs = new LogRecordCapture();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var bridge = CreateBridge(loggerFactory.CreateLogger<VoiceConversationBridge>());

        await StartAndQueueAsync(bridge, "link-camp-ids-2");

        saved.Should().ContainSingle("the Conversation is still created and marked with its campaign");
        saved[0].Metadata.Should().ContainKey("campaignId").WhoseValue.Should().Be("11");
        saved[0].Metadata.Should().NotContainKey("dialerContactId");
        saved[0].Metadata.Should().NotContainKey("dialerActionId");
        logs.Records.Where(r => r.Level == LogLevel.Warning).Should().ContainSingle(
            "a campaign call whose dialer ids cannot be read is logged once");
    }

    /// <summary>
    /// Records every Conversation saved, and serves the last one back by LinkedId, so a call can be driven through
    /// start, queue, connect and end against one stored Conversation.
    /// </summary>
    private List<Conversation> TrackConversations(string linkedId)
    {
        var saved = new List<Conversation>();
        _conversations.SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var conversation = ci.Arg<Conversation>();
                if (!saved.Contains(conversation))
                    saved.Add(conversation);
                return Task.CompletedTask;
            });
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), linkedId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(saved.LastOrDefault()));
        return saved;
    }

    /// <summary>One resolvable voice contact per address; the map turns a ContactId back into its address.</summary>
    private Dictionary<EntityId, string> StubContactsFor(params string[] addresses)
    {
        var byId = new Dictionary<EntityId, string>();
        foreach (var address in addresses)
        {
            var contact = new Contact { ContactId = EntityId.New(), TenantId = new TenantId(Tenant), CreatedAt = _clock.UtcNow };
            _contacts.ResolveAsync(
                    Arg.Any<TenantId>(),
                    Arg.Is<ChannelAddress>(a => a != null && a.Channel == ChannelType.Voice && a.Address == address),
                    Arg.Any<CancellationToken>())
                .Returns(contact);
            byId[contact.ContactId] = address;
        }

        return byId;
    }

    private async Task StartAndQueueAsync(VoiceConversationBridge bridge, string linkedId)
    {
        await bridge.HandleEventAsync(new CallStartedEvent($"s-{linkedId}", "primary", _clock.UtcNow, CallDirection.Inbound, null));
        await bridge.HandleEventAsync(new CallQueuedEvent($"s-{linkedId}", "primary", _clock.UtcNow, "acme-Support", 1));
    }

    [Fact]
    public async Task HandleEventAsync_ShouldMarkTheConversationWithItsCampaign_WhenTheLegCarriesDialerCampaignId()
    {
        StubLegVariables(campaignId: "11", dialedNumber: DialedNumber);
        TrunkSession("link-camp-1", CampaignCallerId);
        StubContactsFor(DialedNumber, CampaignCallerId, "anonymous");
        var saved = TrackConversations("link-camp-1");
        var bridge = CreateBridge();

        await StartAndQueueAsync(bridge, "link-camp-1");

        saved.Should().ContainSingle("the campaign call queued once");
        saved[0].Channel.Should().Be(ChannelType.Voice);
        saved[0].TenantId.Value.Should().Be(Tenant);
        saved[0].Metadata.Should().ContainKey("campaignId").WhoseValue.Should().Be("11");
    }

    [Fact]
    public async Task HandleEventAsync_ShouldNotMarkACampaign_WhenTheLegHasNoDialerCampaignId()
    {
        StubLegVariables(campaignId: null, dialedNumber: null);
        TrunkSession("link-camp-2", "+15551230000");
        StubContactsFor("+15551230000", "anonymous");
        var saved = TrackConversations("link-camp-2");
        var bridge = CreateBridge();

        await StartAndQueueAsync(bridge, "link-camp-2");

        saved.Should().ContainSingle("the inbound call queued once");
        saved[0].Metadata.Should().NotContainKey("campaignId");
    }

    [Fact]
    public async Task HandleEventAsync_ShouldAttachTheCampaignConversationToTheDialedCustomer_WhenTheCallerIdIsTheCampaignsOutboundNumber()
    {
        StubLegVariables(campaignId: "11", dialedNumber: DialedNumber);
        TrunkSession("link-camp-3", CampaignCallerId);
        var addressOf = StubContactsFor(DialedNumber, CampaignCallerId, "anonymous");
        var saved = TrackConversations("link-camp-3");
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(AgentInState(AgentState.Available));
        var events = CaptureEvents();
        var bridge = CreateBridge();

        await StartAndQueueAsync(bridge, "link-camp-3");
        await bridge.HandleEventAsync(new CallConnectedEvent("s-link-camp-3", "primary", _clock.UtcNow, null, "acme-Support", TimeSpan.FromSeconds(3)));

        saved.Should().ContainSingle();
        addressOf[saved[0].ContactId].Should().Be(DialedNumber,
            "a campaign Conversation belongs to the customer the campaign dialed, not to the campaign's own caller id");
        await _contacts.DidNotReceive().ResolveAsync(
            Arg.Any<TenantId>(),
            Arg.Is<ChannelAddress>(a => a != null && a.Address == CampaignCallerId),
            Arg.Any<CancellationToken>());
        events.OfType<VoiceScreenPopEvent>().Should().ContainSingle()
            .Which.CallerNumber.Should().Be(DialedNumber);
    }

    [Fact]
    public async Task HandleEventAsync_ShouldAttachTheCampaignConversationToTheDialedCustomer_WhenTheCallerIdIsAbsent()
    {
        StubLegVariables(campaignId: "11", dialedNumber: DialedNumber);
        TrunkSession("link-camp-4", callerIdNum: null);
        var addressOf = StubContactsFor(DialedNumber, "anonymous");
        var saved = TrackConversations("link-camp-4");
        var bridge = CreateBridge();

        await StartAndQueueAsync(bridge, "link-camp-4");

        saved.Should().ContainSingle();
        addressOf[saved[0].ContactId].Should().Be(DialedNumber,
            "a campaign call with no caller id still belongs to the dialed customer, never to the anonymous bucket");
    }

    [Fact]
    public async Task HandleEventAsync_ShouldFallBackToTheCallerIdAndWarn_WhenDialerNumberCannotBeRead()
    {
        StubLegVariables(campaignId: "11", dialedNumber: null);
        TrunkSession("link-camp-5", CampaignCallerId);
        var addressOf = StubContactsFor(DialedNumber, CampaignCallerId, "anonymous");
        var saved = TrackConversations("link-camp-5");
        var logs = new LogRecordCapture();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var bridge = CreateBridge(loggerFactory.CreateLogger<VoiceConversationBridge>());

        await StartAndQueueAsync(bridge, "link-camp-5");

        saved.Should().ContainSingle("the Conversation is still created when the dialed number cannot be read");
        addressOf[saved[0].ContactId].Should().Be(CampaignCallerId, "with no dialed number the caller-id rule applies");
        logs.Records.Where(r => r.Level == LogLevel.Warning).Should().ContainSingle(
            "falling back to the caller id on a campaign call is logged once");
    }

    [Fact]
    public async Task OnCallEnded_ShouldStampTheDialedNumberAsCallbackNumber_WhenTheCallIsACampaignCall()
    {
        StubLegVariables(campaignId: "11", dialedNumber: DialedNumber);
        var session = MakeSession("link-camp-6", tenant: null, AgentInterface);
        session.QueueName = "acme-Support";
        session.ConnectedAt = _clock.UtcNow.AddSeconds(-30);
        // The customer's trunk leg (the campaign's caller id), then the agent leg, which drops abnormally first.
        AddLeg(session, CampaignTrunkLeg, ParticipantRole.Caller, CampaignCallerId,
            HangupCause.NormalClearing, _clock.UtcNow.AddSeconds(2));
        AddLeg(session, CampaignAgentLeg, ParticipantRole.Destination, null,
            HangupCause.NetworkOutOfOrder, _clock.UtcNow);
        _sessions.GetById("s-link-camp-6").Returns(session);
        StubContactsFor(DialedNumber, CampaignCallerId, "anonymous");
        var saved = TrackConversations("link-camp-6");
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(AgentInState(AgentState.Available));
        var bridge = CreateBridge();

        await StartAndQueueAsync(bridge, "link-camp-6");
        await bridge.HandleEventAsync(new CallConnectedEvent("s-link-camp-6", "primary", _clock.UtcNow, null, "acme-Support", TimeSpan.FromSeconds(3)));
        await bridge.HandleEventAsync(new CallEndedEvent("s-link-camp-6", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(30)));

        saved.Should().ContainSingle();
        saved[0].Metadata.Should().ContainKey("agentLegAbnormal").WhoseValue.Should().Be("true");
        saved[0].Metadata.Should().ContainKey("callbackNumber").WhoseValue.Should().Be(DialedNumber,
            "a callback to a campaign customer dials the customer, not the campaign's own outbound number");
    }

    // ─── First capacity use after boot (#360) ──────────────────────────────────
    //
    // PersistentAgentCapacityService rebuilds its in-memory load from the Active Conversations on its
    // first use in the process. When that first use is the reservation for a call whose Conversation is
    // already saved Active, the rebuild counts the call and the reservation counts it again: voice load 2
    // during the call and a stuck 1 after the hangup. These tests run the real persistent service over a
    // store that keeps a copy of each saved row, so the rebuild sees only what was saved before it ran.

    private Dictionary<EntityId, Conversation> UseSnapshotConversationStore()
    {
        var rows = new Dictionary<EntityId, Conversation>();
        _conversations.When(s => s.SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>()))
            .Do(ci => { var c = ci.Arg<Conversation>(); rows[c.ConversationId] = Snapshot(c); });
        _conversations.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(ci => rows.TryGetValue(ci.ArgAt<EntityId>(1), out var c) ? Snapshot(c) : null);
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => rows.Values.Where(c => c.VoiceLinkedId == ci.ArgAt<string>(1)).Select(Snapshot).FirstOrDefault());
        _conversations.ListByStateAsync(Arg.Any<TenantId>(), Arg.Any<ConversationState>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ActiveRows(rows, ci.ArgAt<ConversationState>(1)));
        return rows;
    }

    private static IReadOnlyList<Conversation> ActiveRows(Dictionary<EntityId, Conversation> rows, ConversationState state) =>
        [.. rows.Values.Where(c => c.State == state).Select(Snapshot)];

    private static Conversation Snapshot(Conversation source)
    {
        var copy = new Conversation
        {
            ConversationId = source.ConversationId,
            TenantId = source.TenantId,
            ContactId = source.ContactId,
            Channel = source.Channel,
            Owner = source.Owner,
            State = source.State,
            CreatedAt = source.CreatedAt,
            ClosedAt = source.ClosedAt,
            UpdatedAt = source.UpdatedAt,
            VoiceLinkedId = source.VoiceLinkedId,
        };
        foreach (var (key, value) in source.Metadata)
            copy.SetMetadata(key, value);
        return copy;
    }

    private PersistentAgentCapacityService FreshPersistentCapacity()
    {
        var resolver = Substitute.For<IAgentCapacityResolver>();
        resolver.ResolveAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(new ChannelCapacity());
        return new PersistentAgentCapacityService(resolver, Substitute.For<IAgentCapacityStore>(), _conversations);
    }

    [Fact]
    public async Task OnCallConnected_ShouldLeaveVoiceLoadZeroAfterHangup_WhenFirstCapacityUseAfterBoot()
    {
        var rows = UseSnapshotConversationStore();
        var queued = VoiceConversation("link-boot", ConversationState.Queued);
        rows[queued.ConversationId] = queued;
        _sessions.GetById("s-link-boot").Returns(MakeSession("link-boot", Tenant, AgentInterface));
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(AgentInState(AgentState.Available));
        var capacity = FreshPersistentCapacity();
        var bridge = CreateBridge(capacity: capacity);
        var tenantId = new TenantId(Tenant);
        var agentId = EntityId.From(AgentGuid);

        await bridge.HandleEventAsync(new CallConnectedEvent("s-link-boot", "primary", _clock.UtcNow, null, "acme-Support", TimeSpan.FromSeconds(3)));
        var duringCall = await capacity.GetCurrentLoadAsync(tenantId, agentId, ChannelType.Voice, CancellationToken.None);
        await bridge.HandleEventAsync(new CallEndedEvent("s-link-boot", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(39)));
        var afterHangup = await capacity.GetCurrentLoadAsync(tenantId, agentId, ChannelType.Voice, CancellationToken.None);

        rows[queued.ConversationId].State.Should().Be(ConversationState.WrapUp);
        duringCall.Should().Be(1, "the answered call is one unit of voice load, counted once");
        afterHangup.Should().Be(0, "the hangup releases the only unit the call held");
    }

    [Fact]
    public async Task OnCallConnected_ShouldReleaseTheReservation_WhenSavingTheActiveConversationFails()
    {
        var conversation = VoiceConversation("link-save-fails", ConversationState.Queued);
        _sessions.GetById("s-link-save-fails").Returns(MakeSession("link-save-fails", Tenant, AgentInterface));
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-save-fails", Arg.Any<CancellationToken>())
            .Returns(conversation);
        _conversations.SaveAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("store down"));
        var bridge = CreateBridge();

        var act = () => bridge.HandleEventAsync(new CallConnectedEvent("s-link-save-fails", "primary", _clock.UtcNow, null, "acme-Support", TimeSpan.FromSeconds(3)));

        await act.Should().NotThrowAsync("the handler logs a failed event instead of stopping the host");
        await _capacity.Received(1).ReserveAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), ChannelType.Voice, Arg.Any<CancellationToken>());
        await _capacity.Received(1).ReleaseAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), ChannelType.Voice, Arg.Any<CancellationToken>());
        await _agents.DidNotReceive().SaveAsync(Arg.Any<Agent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnCallConnected_ShouldStillActivateAndNotRelease_WhenReservingCapacityFails()
    {
        var conversation = VoiceConversation("link-reserve-fails", ConversationState.Queued);
        _sessions.GetById("s-link-reserve-fails").Returns(MakeSession("link-reserve-fails", Tenant, AgentInterface));
        _conversations.FindByVoiceLinkedIdAsync(Arg.Any<TenantId>(), "link-reserve-fails", Arg.Any<CancellationToken>())
            .Returns(conversation);
        _capacity.ReserveAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("capacity store down"));
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(AgentInState(AgentState.Available));
        var bridge = CreateBridge();

        await bridge.HandleEventAsync(new CallConnectedEvent("s-link-reserve-fails", "primary", _clock.UtcNow, null, "acme-Support", TimeSpan.FromSeconds(3)));

        await _conversations.Received(1).SaveAsync(Arg.Is<Conversation>(c => c != null && c.State == ConversationState.Active), Arg.Any<CancellationToken>());
        await _capacity.DidNotReceive().ReleaseAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
        await _agents.Received(1).SaveAsync(Arg.Is<Agent>(a => a != null && a.State == AgentState.Busy), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkOutboundCall_ShouldLeaveVoiceLoadZeroAfterHangup_WhenFirstCapacityUseAfterBoot()
    {
        AddPrimaryServer();
        StubContact();
        var rows = UseSnapshotConversationStore();
        var agentId = EntityId.From(AgentGuid);
        var tenantId = new TenantId(Tenant);
        _agents.GetByIdAsync(Arg.Any<TenantId>(), Arg.Is<EntityId>(id => id.Value == AgentGuid), Arg.Any<CancellationToken>())
            .Returns(AgentInState(AgentState.Available));
        var capacity = FreshPersistentCapacity();
        var leader = LeaderStub(isLeader: true);
        var dial = new AgentOutboundDialService(
            _conversations, capacity, _contacts, _agents, Substitute.For<Verbara.Sdk.Pro.MultiTenant.ITenantStore>(),
            new AcceptingOriginateExecutor(),
            OutboundTrunk.TrunkFakes.Resolver(null, new OutboundTrunk.FakeTrunkStore().Add(Tenant, OutboundTrunk.TrunkFakes.IpTrunk(7, "pstn-default")), "pstn-default"),
            null, leader, _clock, NullLogger<AgentOutboundDialService>.Instance);
        var bridge = CreateBridge(capacity: capacity);

        var outcome = await dial.DialAsync(tenantId, agentId, "+15551234567", null, CancellationToken.None);
        outcome.Accepted.Should().BeTrue();
        StubGetVarFor("VERBARA_OUTBOUND_ID", outcome.CorrelationId);
        StubGetVarFor("TENANT_ID", Tenant);
        var session = new CallSession("s-out-boot", "link-out-boot", "primary", CallDirection.Inbound);
        _sessions.GetById("s-out-boot").Returns(session);

        await bridge.LinkOutboundCallAsync(session, "PJSIP/acme-agent-x");
        var duringCall = await capacity.GetCurrentLoadAsync(tenantId, agentId, ChannelType.Voice, CancellationToken.None);
        await bridge.HandleEventAsync(new CallEndedEvent("s-out-boot", "primary", _clock.UtcNow, null, TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(39)));
        var afterHangup = await capacity.GetCurrentLoadAsync(tenantId, agentId, ChannelType.Voice, CancellationToken.None);

        rows[EntityId.From(outcome.CorrelationId!)].State.Should().Be(ConversationState.WrapUp);
        duringCall.Should().Be(1, "the placed call is one unit of voice load, counted once");
        afterHangup.Should().Be(0, "the hangup releases the only unit the call held");
    }

    // ─── Delivery under the Sdk's call lock ─────────────────────────────────────────────────────
    // The Sdk raises CallStarted and CallQueued with the call's lock held, so OnEvent must hand off and return. The
    // pair below is the real one: CallQueued marks the Conversation with the campaign CallStarted stamped on the
    // session, so the mark also proves the order.

    [Fact]
    public async Task OnEvent_ShouldReturnWithoutWaiting_WhenAnotherHandlerHoldsTheCallGate()
    {
        const string linkedId = "link-gate";
        var bound = TimeSpan.FromSeconds(5);
        StubLegVariables(campaignId: "11", dialedNumber: DialedNumber);
        TrunkSession(linkedId, CampaignCallerId);
        StubContactsFor(DialedNumber, CampaignCallerId, "anonymous");
        var saved = TrackConversations(linkedId);
        var bridge = CreateBridge();

        // CallStarted's first read is left unanswered, which suspends its handler with the gate held.
        var readReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readAnswer = new TaskCompletionSource<GetVarResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CA2012 // ValueTask in NSubstitute setup
        _ami.SendActionAsync<GetVarResponse>(
                Arg.Is<ManagerAction>(a => a is GetVarAction && ((GetVarAction)a).Variable == "VERBARA_OUTBOUND_ID"),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                readReached.TrySetResult();
                return new ValueTask<GetVarResponse>(readAnswer.Task);
            });
#pragma warning restore CA2012
        var queuedHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var created = _eventBus.Events.Subscribe(e =>
        {
            if (e is ConversationStateChangedEvent)
                queuedHandled.TrySetResult();
        });

        // Delivered off the test thread: a delivery that blocks must fail the bound, not hang the run.
        Func<Task> deliverStarted = () => Task.Run(() =>
            bridge.OnEvent(new CallStartedEvent($"s-{linkedId}", "primary", _clock.UtcNow, CallDirection.Inbound, null)));
        Func<Task> deliverQueued = () => Task.Run(() =>
            bridge.OnEvent(new CallQueuedEvent($"s-{linkedId}", "primary", _clock.UtcNow, "acme-Support", 1)));

        try
        {
            await deliverStarted.Should().CompleteWithinAsync(bound, "the first handler suspends on its AMI read");
            await readReached.Task.WaitAsync(bound);

            await deliverQueued.Should().CompleteWithinAsync(bound,
                "a delivery runs under the Sdk's call lock and must not wait for the handler holding the gate");
            saved.Should().BeEmpty("the second event waits its turn behind the first handler's AMI read");
        }
        finally
        {
            // Answered on every path, so a failing run does not leave a delivery blocked behind the gate.
            readAnswer.TrySetResult(new GetVarResponse { Response = "Error" });
        }

        await queuedHandled.Task.WaitAsync(bound);
        saved.Should().ContainSingle("both events are handled, and the call queued once");
        saved[0].Metadata.Should().ContainKey("campaignId").WhoseValue.Should().Be("11",
            "CallStarted finished stamping the campaign before CallQueued read it");
    }

    private sealed class AcceptingOriginateExecutor : Verbara.Sdk.Pro.Dialer.Execution.OriginateExecutorBase
    {
        protected override ValueTask<Verbara.Sdk.Pro.Dialer.Execution.OriginateResult> ExecuteCoreAsync(
            OriginateAction action, string nodeId, CancellationToken ct) =>
            ValueTask.FromResult(new Verbara.Sdk.Pro.Dialer.Execution.OriginateResult(true, "act-boot", null));
    }

    public void Dispose() => _eventBus.Dispose();
}
