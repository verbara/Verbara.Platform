using NSubstitute;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Execution;
using H = Verbara.Platform.Api.Tests.SupervisorListen.ListenHarness;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen 2.3 (design D1, D6, D7, D9, D10, D11): the listen core — start, current, stop and the
/// registry — with a substitute executor, a stub leader and in-memory stores. The route-level refusals that precede
/// the service (<c>not-permitted</c>, <c>invalid-destination</c>, <c>voice-unavailable</c>) are covered by
/// <c>SupervisorListenEndpointsTests</c>.
/// </summary>
public sealed class SupervisorListenServiceTests
{
    private readonly H _h = new();

    private Task<SupervisorListenResult> StartAsync(Conversation conversation, string destination = "endpoint") =>
        _h.Service().StartAsync(H.Caller(), H.TenantId, H.Supervisor, conversation.ConversationId.Value, destination, CancellationToken.None);

    // ─── Refusals, in the fixture's precedence order ────────────────────────────

    [Fact]
    public async Task Start_ShouldRefuseNotLeader_AndSendNoOriginate_WhenThisReplicaIsAFollower()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        _h.Leader.IsLeader.Returns(false);

        var result = await StartAsync(call);

        result.Refusal!.Error.Should().Be(SupervisorListenRefusal.NotLeader);
        result.Refusal.ConversationId.Should().Be(call.ConversationId.Value);
        _h.Executor.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_ShouldRefuseConversationNotFound_WhenTheIdIsUnknown()
    {
        var result = await _h.Service().StartAsync(H.Caller(), H.TenantId, H.Supervisor, "nope", "endpoint", CancellationToken.None);

        result.Refusal!.Error.Should().Be(SupervisorListenRefusal.ConversationNotFound);
        result.Refusal.ConversationId.Should().Be("nope");
        _h.Executor.Actions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("")]
    public async Task Start_ShouldRefuseConversationNotFound_WhenTheIdIsBlank(string id)
    {
        await _h.SeedEndpointAsync();

        var result = await _h.Service().StartAsync(H.Caller(), H.TenantId, H.Supervisor, id, "endpoint", CancellationToken.None);

        result.Refusal!.Error.Should().Be(SupervisorListenRefusal.ConversationNotFound, "a blank id names no conversation");
        result.Refusal.ConversationId.Should().Be(id);
        _h.Executor.Actions.Should().BeEmpty();
        _h.Registry.GetBySupervisor(H.TenantId, H.Supervisor).Should().BeNull();
    }

    [Fact]
    public async Task Start_ShouldRefuseConversationNotFound_WhenTheCallBelongsToAnotherTenant()
    {
        var foreign = _h.SeedVoiceCall(tenant: new TenantId("globex"));
        await _h.SeedEndpointAsync();

        var result = await StartAsync(foreign);

        result.Refusal!.Error.Should().Be(SupervisorListenRefusal.ConversationNotFound, "another tenant's call is invisible");
        _h.Executor.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_ShouldRefuseNoVoiceCall_WhenTheConversationIsWebChat()
    {
        var chat = new Conversation
        {
            ConversationId = EntityId.New(), TenantId = H.TenantId, ContactId = EntityId.New(),
            Channel = ChannelType.WebChat, State = ConversationState.Active, CreatedAt = _h.Clock.UtcNow,
            Owner = new ConversationOwner(ConversationOwnerKind.Agent, H.Owner),
        };
        _h.Conversations.GetByIdAsync(H.TenantId, chat.ConversationId, Arg.Any<CancellationToken>()).Returns(chat);

        (await StartAsync(chat)).Refusal!.Error.Should().Be(SupervisorListenRefusal.NoVoiceCall);
    }

    [Theory]
    [InlineData(ConversationState.Queued)]
    [InlineData(ConversationState.WrapUp)]
    [InlineData(ConversationState.Closed)]
    public async Task Start_ShouldRefuseNoVoiceCall_WhenTheCallIsNotActiveOrOnHold(ConversationState state)
    {
        var call = _h.SeedVoiceCall(state);

        (await StartAsync(call)).Refusal!.Error.Should().Be(SupervisorListenRefusal.NoVoiceCall);
    }

    [Fact]
    public async Task Start_ShouldAccept_WhenTheCallIsOnHold()
    {
        var call = _h.SeedVoiceCall(ConversationState.OnHold);
        await _h.SeedEndpointAsync();

        (await StartAsync(call)).Listen.Should().NotBeNull();
    }

    [Fact]
    public async Task Start_ShouldRefuseNoVoiceCall_WhenTheAgentLegIsGone()
    {
        var call = _h.SeedVoiceCall();
        _h.CallLegs.Legs[H.LinkedId] = null;

        (await StartAsync(call)).Refusal!.Error.Should().Be(SupervisorListenRefusal.NoVoiceCall);
        _h.Executor.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_ShouldRefuseAlreadyListening_NamingTheExistingListen()
    {
        var first = _h.SeedVoiceCall();
        var second = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        var existing = (await StartAsync(first)).Listen!;

        var result = await StartAsync(second);

        result.Refusal!.Error.Should().Be(SupervisorListenRefusal.AlreadyListening);
        result.Refusal.ConversationId.Should().Be(first.ConversationId.Value);
        result.Refusal.ListenId.Should().Be(existing.ListenId);
        _h.Executor.Actions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Start_ShouldRefuseNoSupervisorEndpoint_WhenTheCallerHasNoCredentials()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedPhoneAsync();   // a profile with a phone but no endpoint credentials

        var result = await StartAsync(call, "endpoint");

        result.Refusal!.Error.Should().Be(SupervisorListenRefusal.NoSupervisorEndpoint);
        _h.Registry.GetBySupervisor(H.TenantId, H.Supervisor).Should().BeNull("a refused start leaves no reservation");
    }

    [Fact]
    public async Task Start_ShouldRefuseNoListenPhone_WhenNoNumberIsStored()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();

        (await StartAsync(call, "phone")).Refusal!.Error.Should().Be(SupervisorListenRefusal.NoListenPhone);
        _h.Executor.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_ShouldRefuseNoTrunk_WhenNoRouteAndNoDefaultTrunk()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedPhoneAsync();
        _h.Trunk = null;

        (await StartAsync(call, "phone")).Refusal!.Error.Should().Be(SupervisorListenRefusal.NoTrunk);
        _h.Executor.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_ShouldRefuseOriginateFailed_AndReleaseTheSlot_WhenAsteriskRefuses()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        _h.Executor.Result = new OriginateResult(false, "act-1", "Originate failed");

        var result = await StartAsync(call);

        result.Refusal!.Error.Should().Be(SupervisorListenRefusal.OriginateFailed);
        _h.Registry.GetBySupervisor(H.TenantId, H.Supervisor).Should().BeNull();
        await _h.Audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default!, default!, default!, default!);
    }

    [Fact]
    public async Task Start_ShouldRefuseOriginateFailed_WhenTheExecutorThrows()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        _h.Executor.Throw = new InvalidOperationException("socket closed");

        (await StartAsync(call)).Refusal!.Error.Should().Be(SupervisorListenRefusal.OriginateFailed);
    }

    // ─── The originate's shape ──────────────────────────────────────────────────

    [Fact]
    public async Task Start_ShouldOriginateTheSupervisorEndpointIntoTheListenContext()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();

        var listen = (await StartAsync(call)).Listen!;

        var action = _h.Executor.Actions.Single();
        action.Channel.Should().Be($"PJSIP/acme-agent-sup-{H.Supervisor.Value}");
        action.CallerId.Should().Be($"\"Verbara Listen\" <{call.ConversationId.Value}>");
        action.Context.Should().Be("supervisor-listen");
        action.Exten.Should().Be("endpoint");
        action.Priority.Should().Be(1);
        action.IsAsync.Should().BeTrue();
        action.ChannelId.Should().Be(listen.ListenId, "the supervisor leg's uniqueid IS the listen id");
        H.Vars(action).Should().Equal([$"SPY_TARGET={H.AgentLegUniqueId}"], "no VERBARA_OUTBOUND_ID: the bridge never tracks the leg");
        listen.State.Should().Be("dialing");
        listen.ListeningSince.Should().BeNull();
        listen.Destination.Should().Be("endpoint");
        listen.ConversationId.Should().Be(call.ConversationId.Value);
        listen.StartedAt.Should().Be(_h.Clock.UtcNow);
    }

    [Fact]
    public async Task Start_ShouldDialTheStoredNumberOverTheTrunk_WhenTheDestinationIsPhone()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedPhoneAsync("+573001234567");

        var listen = (await StartAsync(call, "phone")).Listen!;

        var action = _h.Executor.Actions.Single();
        action.Channel.Should().Be("PJSIP/+573001234567@pstn-default", "lab 0.3(g): PJSIP/{number}@{trunk}");
        action.CallerId.Should().Be("+15558675309", "the tenant's outbound caller ID");
        action.Context.Should().Be("supervisor-listen");
        action.Exten.Should().Be("phone");
        action.ChannelId.Should().Be(listen.ListenId);
        H.Vars(action).Should().Equal([$"SPY_TARGET={H.AgentLegUniqueId}"]);
    }

    [Fact]
    public async Task Start_ShouldPreferTheOutboundRouteTrunk_OverTheDefault()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedPhoneAsync("+573001234567");
        _h.Routes = new H.FakeRoutes { Trunk = new Verbara.Sdk.Pro.Dialer.Models.Trunk { Name = "co-mobile" } };

        await StartAsync(call, "phone");

        _h.Executor.Actions.Single().Channel.Should().Be("PJSIP/+573001234567@co-mobile");
        _h.Routes.Asked.Should().Equal("+573001234567");
    }

    // ─── Current, stop, silence and audit ───────────────────────────────────────

    [Fact]
    public async Task Current_ShouldAnswerTheCallersListen_AndListenNotFoundOtherwise()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        var service = _h.Service();

        service.GetCurrent(H.TenantId, H.Supervisor).Refusal!.Error.Should().Be(SupervisorListenRefusal.ListenNotFound);
        var started = (await service.StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None)).Listen!;

        var current = service.GetCurrent(H.TenantId, H.Supervisor);
        current.Listen.Should().BeEquivalentTo(started);
        var none = service.GetCurrent(H.TenantId, EntityId.From("someone-else"));
        none.Refusal!.Error.Should().Be(SupervisorListenRefusal.ListenNotFound);
        none.Refusal.ConversationId.Should().BeNull("current has no route conversation");
    }

    [Fact]
    public async Task CurrentAndStop_ShouldRefuseNotLeader_OnAFollower()
    {
        _h.Leader.IsLeader.Returns(false);
        var service = _h.Service();

        service.GetCurrent(H.TenantId, H.Supervisor).Refusal!.Error.Should().Be(SupervisorListenRefusal.NotLeader);
        (await service.StopAsync(H.Caller(), H.TenantId, H.Supervisor, "c1", CancellationToken.None))
            .Refusal!.Error.Should().Be(SupervisorListenRefusal.NotLeader);
    }

    [Fact]
    public async Task Stop_ShouldEndTheListen_HangUpOnlyItsLeg_AndAnswerStopped()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        var service = _h.Service();
        var listen = (await service.StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None)).Listen!;
        _h.Clock.UtcNow = _h.Clock.UtcNow.AddSeconds(30);

        var stopped = await service.StopAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, CancellationToken.None);

        stopped.Stopped!.ListenId.Should().Be(listen.ListenId);
        stopped.Stopped.ConversationId.Should().Be(call.ConversationId.Value);
        stopped.Stopped.EndReason.Should().Be("stopped");
        stopped.Stopped.StoppedAt.Should().Be(_h.Clock.UtcNow);
        _h.LegControl.HungUp.Select(l => l.ListenId).Should().Equal(listen.ListenId);
        service.GetCurrent(H.TenantId, H.Supervisor).Refusal!.Error.Should().Be(SupervisorListenRefusal.ListenNotFound);
    }

    [Fact]
    public async Task Stop_ShouldRefuseListenNotFound_WhenTheListenIsOnAnotherConversation()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        var service = _h.Service();
        await service.StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None);

        var result = await service.StopAsync(H.Caller(), H.TenantId, H.Supervisor, "other-conversation", CancellationToken.None);

        result.Refusal!.Error.Should().Be(SupervisorListenRefusal.ListenNotFound);
        result.Refusal.ConversationId.Should().Be("other-conversation", "stop echoes its route's conversation id");
        _h.LegControl.HungUp.Should().BeEmpty();
    }

    [Fact]
    public async Task Listen_ShouldNeverChangeTheConversation()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        var ownerBefore = call.Owner;
        var stateBefore = call.State;
        var metadataBefore = call.Metadata.ToDictionary();
        var service = _h.Service();

        await service.StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None);
        service.GetCurrent(H.TenantId, H.Supervisor);
        await service.StopAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, CancellationToken.None);

        await _h.Conversations.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        call.Owner.Should().Be(ownerBefore);
        call.State.Should().Be(stateBefore);
        call.Metadata.Should().Equal(metadataBefore);
    }

    [Fact]
    public async Task Start_ShouldAuditListenStartedOnce_WithTheDesignMetadata()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();

        var listen = (await StartAsync(call)).Listen!;

        await _h.Audit.Received(1).RecordAsync(
            H.TenantId, "conversations", "supervision.listen_started", "info",
            H.Supervisor.Value, "user", call.ConversationId.Value, "Conversation",
            Arg.Any<Guid?>(), Arg.Any<Verbara.Platform.Audit.AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m =>
                m["listen_id"] == listen.ListenId && m["destination"] == "endpoint"
                && m["agent_id"] == H.Owner.Value && m["supervisor_user_id"] == H.Supervisor.Value
                && !m.ContainsKey("phone_last4")),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_ShouldAuditOnlyTheLastFourDigits_OfAPhoneListen()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedPhoneAsync("+573001234567");

        await StartAsync(call, "phone");

        await _h.Audit.Received(1).RecordAsync(
            H.TenantId, "conversations", "supervision.listen_started", "info",
            Arg.Any<string>(), "user", Arg.Any<string?>(), "Conversation",
            Arg.Any<Guid?>(), Arg.Any<Verbara.Platform.Audit.AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m =>
                m["phone_last4"] == "4567" && m.Values.All(v => !v.Contains("3001234567"))),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_ShouldAuditListenStoppedOnce_WithTheCallerAsActor()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        var service = _h.Service();
        var listen = (await service.StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "endpoint", CancellationToken.None)).Listen!;
        _h.Registry.MarkListening(listen.ListenId, _h.Clock.UtcNow.AddSeconds(2));
        _h.Clock.UtcNow = _h.Clock.UtcNow.AddSeconds(12);

        await service.StopAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, CancellationToken.None);
        await service.StopAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, CancellationToken.None);

        await _h.Audit.Received(1).RecordAsync(
            H.TenantId, "conversations", "supervision.listen_stopped", "info",
            H.Supervisor.Value, "user", call.ConversationId.Value, "Conversation",
            Arg.Any<Guid?>(), Arg.Any<Verbara.Platform.Audit.AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m =>
                m["listen_id"] == listen.ListenId && m["end_reason"] == "stopped" && m["listened_seconds"] == "10"),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_ShouldSucceed_WhenTheAuditWriteFails()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();
        _h.Audit.RecordAsync(default!, default!, default!, default!, default!, default!)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("audit down")));

        (await StartAsync(call)).Listen.Should().NotBeNull("a failed audit write never fails the listen");
    }

    [Fact]
    public async Task Start_ShouldStartObservingTheLeg_OnFirstUse()
    {
        var call = _h.SeedVoiceCall();
        await _h.SeedEndpointAsync();

        await StartAsync(call);

        _h.LegControl.EnsureObservingCalls.Should().BeGreaterThan(0);
    }
}
