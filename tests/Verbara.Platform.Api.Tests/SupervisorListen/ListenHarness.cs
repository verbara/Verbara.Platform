using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Sessions;
using Trunk = Verbara.Sdk.Pro.Dialer.Models.Trunk;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen: a <see cref="SupervisorListenService"/> wired to fakes — a recording originate executor,
/// a stub leader, in-memory registry and profile store, a substitute conversation store, call legs and audit.
/// </summary>
internal sealed class ListenHarness
{
    public const string Tenant = "acme";
    public const string DefaultTrunk = "pstn-default";
    public const string LinkedId = "1712345678.40";
    public const string AgentLegUniqueId = "1712345678.42";
    public static readonly TenantId TenantId = new(Tenant);
    public static readonly EntityId Supervisor = EntityId.From("7d2c9e4b1a5f4c3e8b6d0f1a2c3e4b5d");
    public static readonly EntityId Owner = EntityId.From("0651f84b6f284a1c9b8d658330fa8310");

    public RecordingExecutor Executor { get; } = new();
    public IClusterLeader Leader { get; } = Substitute.For<IClusterLeader>();
    public IConversationStore Conversations { get; } = Substitute.For<IConversationStore>();
    public FakeCallLegs CallLegs { get; } = new();
    public ISupervisorListenProfileStore Profiles { get; set; } = new InMemoryProfiles();
    public ITenantStore Tenants { get; } = Substitute.For<ITenantStore>();
    public IAuditService Audit { get; } = Substitute.For<IAuditService>();
    public FakeLegControl LegControl { get; } = new();
    public FakeRoutes? Routes { get; set; }
    public string? Trunk { get; set; } = DefaultTrunk;
    public MutableClock Clock { get; } = new(new DateTimeOffset(2026, 10, 12, 15, 4, 5, 123, TimeSpan.Zero));
    public SupervisorListenRegistry Registry { get; } = new();

    public ListenHarness()
    {
        Leader.IsLeader.Returns(true);
        Tenants.GetAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = Tenant, Name = "Acme", Options = new TenantOptions { OutboundCallerId = "+15558675309" } });
    }

    public static ClaimsPrincipal Caller(string userId = "7d2c9e4b1a5f4c3e8b6d0f1a2c3e4b5d") =>
        new(new ClaimsIdentity([new Claim("user_id", userId)], "test"));

    public SupervisorListenAudit AuditWriter() => new(Audit, NullLogger<SupervisorListenAudit>.Instance);

    public SupervisorListenService Service() => new(
        Conversations, Profiles, CallLegs, Executor, Routes, Tenants, Leader, Clock, Trunk, Registry, AuditWriter(),
        LegControl, NullLogger<SupervisorListenService>.Instance);

    /// <summary>A live voice conversation owned by <see cref="Owner"/>, whose agent leg is live.</summary>
    public Conversation SeedVoiceCall(ConversationState state = ConversationState.Active, TenantId? tenant = null)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = tenant ?? TenantId,
            ContactId = EntityId.New(),
            Channel = ChannelType.Voice,
            State = state,
            Owner = new ConversationOwner(ConversationOwnerKind.Agent, Owner),
            VoiceLinkedId = LinkedId,
            CreatedAt = Clock.UtcNow,
        };
        Conversations.GetByIdAsync(conversation.TenantId, conversation.ConversationId, Arg.Any<CancellationToken>())
            .Returns(conversation);
        CallLegs.Legs[LinkedId] =
        [
            new SessionParticipant { UniqueId = "1712345678.40", Channel = "PJSIP/pstn-default-00000001", Technology = "PJSIP", Role = ParticipantRole.Caller },
            new SessionParticipant { UniqueId = AgentLegUniqueId, Channel = $"PJSIP/acme-agent-{Owner.Value}-00000002", Technology = "PJSIP", Role = ParticipantRole.Agent },
        ];
        return conversation;
    }

    public async Task SeedEndpointAsync() => await Profiles.SaveAsync(new SupervisorListenProfile
    {
        TenantId = TenantId,
        UserId = Supervisor,
        SipExtension = "sl3f9a1c07",
        SipPassword = "secret",
        CreatedAt = Clock.UtcNow,
        UpdatedAt = Clock.UtcNow,
    }, CancellationToken.None);

    public async Task SeedPhoneAsync(string phone = "+573001234567")
    {
        var profile = await Profiles.GetAsync(TenantId, Supervisor, CancellationToken.None) ?? new SupervisorListenProfile
        {
            TenantId = TenantId, UserId = Supervisor, CreatedAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow,
        };
        profile.PhoneNumber = phone;
        await Profiles.SaveAsync(profile, CancellationToken.None);
    }

    /// <summary>The <c>Variable</c> headers of an originate, as <c>key=value</c>.</summary>
    public static IReadOnlyList<string> Vars(OriginateAction action) =>
        [.. ((Verbara.Sdk.IHasExtraFields)action).GetExtraFields().Select(kv => kv.Value)];

    public sealed class FakeCallLegs : IVoiceCallLegs
    {
        public Dictionary<string, IReadOnlyList<SessionParticipant>?> Legs { get; } = [];

        public IReadOnlyList<SessionParticipant>? GetParticipants(string linkedId) => Legs.GetValueOrDefault(linkedId);
    }

    public sealed class FakeLegControl : ISupervisorListenLegControl
    {
        public int EnsureObservingCalls { get; private set; }
        public List<ActiveListen> HungUp { get; } = [];

        public void EnsureObserving() => EnsureObservingCalls++;

        public Task HangupAsync(ActiveListen listen, CancellationToken ct)
        {
            HungUp.Add(listen);
            return Task.CompletedTask;
        }
    }

    public sealed class RecordingExecutor : OriginateExecutorBase
    {
        public List<OriginateAction> Actions { get; } = [];
        public OriginateResult Result { get; set; } = new(true, "act-1", null);
        public Exception? Throw { get; set; }

        protected override ValueTask<OriginateResult> ExecuteCoreAsync(OriginateAction action, string nodeId, CancellationToken ct)
        {
            Actions.Add(action);
            if (Throw is not null)
                throw Throw;
            return ValueTask.FromResult(Result);
        }
    }

    public sealed class FakeRoutes : OutboundRouteResolverBase
    {
        public Trunk? Trunk { get; set; }
        public List<string> Asked { get; } = [];

        public override ValueTask<Trunk?> ResolveAsync(string tenantId, string phoneNumber, long? campaignId, CancellationToken ct)
        {
            Asked.Add(phoneNumber);
            return ValueTask.FromResult(Trunk);
        }
    }

    public sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    /// <summary>A minimal in-memory profile store (the real one lives in Storage.InMemory, which is internal).</summary>
    private sealed class InMemoryProfiles : ISupervisorListenProfileStore
    {
        private readonly Dictionary<(string, string), SupervisorListenProfile> _items = [];

        public Task<SupervisorListenProfile?> GetAsync(TenantId tenantId, EntityId userId, CancellationToken ct) =>
            Task.FromResult(_items.GetValueOrDefault((tenantId.Value, userId.Value)));

        public Task<IReadOnlyList<SupervisorListenProfile>> ListWithCredentialsAsync(TenantId tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SupervisorListenProfile>>([.. _items.Values.Where(p => p.TenantId == tenantId && p.HasCredentials)]);

        public Task<bool> SaveAsync(SupervisorListenProfile profile, CancellationToken ct)
        {
            _items[(profile.TenantId.Value, profile.UserId.Value)] = profile;
            return Task.FromResult(true);
        }

        public Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
        {
            _items.Remove((tenantId.Value, userId.Value));
            return Task.CompletedTask;
        }
    }
}
