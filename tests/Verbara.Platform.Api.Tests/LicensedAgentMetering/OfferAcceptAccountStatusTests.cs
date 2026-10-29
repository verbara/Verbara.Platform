using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Auth;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.LicensedAgentMetering;

/// <summary>
/// licensed-agent-metering-follow-ups L17 (tasks.md 2.2, design D3): an offer and its acceptance re-check
/// the agent's account status at the switchboard, before anything changes. (a) is the widened-window control:
/// routing chooses an agent whose user is suspended before the offer is made; (c) offers to an agent id that
/// names no agent. Both drive the composed host's real switchboard and stores.
/// </summary>
public sealed class OfferAccountStatusTests : IClassFixture<AccountStatusApiFactory>
{
    private readonly AccountStatusApiFactory _factory;

    public OfferAccountStatusTests(AccountStatusApiFactory factory) => _factory = factory;

    private IServiceProvider Services => _factory.Services;

    // ─── (a) widened window: suspended between selection and offer ───────────

    [Fact]
    public async Task DistributeAsync_ShouldLeaveConversationQueued_WhenChosenAgentsUserIsSuspendedAfterSelection()
    {
        var tenant = NewTenant();
        var agent = await SeedAgentAsync(tenant);
        var queueId = EntityId.From($"l17-queue-{Guid.NewGuid():N}");
        var conversation = await SeedQueuedConversationAsync(tenant, queueId);
        var bus = Services.GetRequiredService<PlatformEventBus>();
        var offered = new List<ConversationOfferedEvent>();
        using var subscription = bus.Events.Subscribe(e =>
        {
            if (e is ConversationOfferedEvent o && o.ConversationId == conversation.ConversationId.Value)
                lock (offered)
                    offered.Add(o);
        });

        var worker = NewWorker(tenant, new SuspendingSelector(Services.GetRequiredService<IUserStore>(), agent), bus);

        await worker.DistributeAsync(CancellationToken.None);

        var stored = await Services.GetRequiredService<IConversationStore>()
            .GetByIdAsync(tenant, conversation.ConversationId, CancellationToken.None);
        stored!.State.Should().Be(ConversationState.Queued,
            because: "an agent suspended after routing chose it must not receive the offer");
        stored.Owner.Should().Be(ConversationOwner.ForQueue(queueId));
        stored.Metadata.Should().NotContainKey(ConversationOffer.OfferedToKey)
            .And.NotContainKey(ConversationOffer.OfferedAtKey)
            .And.NotContainKey("originQueueId");
        lock (offered)
            offered.Should().BeEmpty(because: "a refused offer publishes no ConversationOfferedEvent");
    }

    // ─── (c) unknown agent id ────────────────────────────────────────────────

    [Fact]
    public async Task OfferToAgentAsync_ShouldFailWithTargetAgentNotFound_WhenAgentDoesNotExist()
    {
        var tenant = NewTenant();
        var queueId = EntityId.From($"l17-queue-{Guid.NewGuid():N}");
        var conversation = await SeedQueuedConversationAsync(tenant, queueId);

        var result = await Services.GetRequiredService<IConversationSwitchboard>().OfferToAgentAsync(
            conversation.ConversationId, tenant, EntityId.From($"ghost-agent-{Guid.NewGuid():N}"), CancellationToken.None);

        result.Success.Should().BeFalse(because: "an id that names no agent cannot be offered work");
        result.FailureReason.Should().Be("Target agent not found.");
        var stored = await Services.GetRequiredService<IConversationStore>()
            .GetByIdAsync(tenant, conversation.ConversationId, CancellationToken.None);
        stored!.State.Should().Be(ConversationState.Queued);
        stored.Metadata.Should().NotContainKey(ConversationOffer.OfferedToKey);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private QueueDistributionWorker NewWorker(TenantId tenant, IAgentSelector selector, PlatformEventBus bus)
    {
        var tenants = Substitute.For<ITenantStore>();
        tenants.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Tenant { TenantId = tenant.Value, Name = tenant.Value, Status = TenantStatus.Active },
        });

        return new QueueDistributionWorker(
            Services.GetRequiredService<IConversationStore>(),
            tenants,
            selector,
            Services.GetRequiredService<IConversationSwitchboard>(),
            bus,
            Services.GetRequiredService<IClock>(),
            new ServiceHeartbeat(),
            Options.Create(new DistributionOptions()),
            NullLogger<QueueDistributionWorker>.Instance);
    }

    // A fresh tenant the host's own distribution worker never visits (it is in no tenant store).
    private static TenantId NewTenant() => new($"l17-{Guid.NewGuid():N}");

    private async Task<Agent> SeedAgentAsync(TenantId tenant)
    {
        var id = Guid.NewGuid().ToString("N");
        var user = new User
        {
            UserId = EntityId.From($"l17-user-{id}"),
            TenantId = tenant,
            Email = $"l17-{id}@example.test",
            DisplayName = "Offer account status",
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Services.GetRequiredService<IUserStore>().CreateAsync(user, CancellationToken.None);
        var agent = new Agent
        {
            AgentId = EntityId.From($"l17-agent-{id}"),
            TenantId = tenant,
            UserId = user.UserId,
            DisplayName = $"Agent {id[..6]}",
            State = AgentState.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Services.GetRequiredService<IAgentStore>().SaveAsync(agent, CancellationToken.None);
        return agent;
    }

    private async Task<Conversation> SeedQueuedConversationAsync(TenantId tenant, EntityId queueId)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"l17-conv-{Guid.NewGuid():N}"),
            TenantId = tenant,
            ContactId = EntityId.From($"l17-contact-{Guid.NewGuid():N}"),
            Channel = ChannelType.WebChat,
            State = ConversationState.Queued,
            Owner = ConversationOwner.ForQueue(queueId),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);
        return conversation;
    }

    /// <summary>
    /// Routing that chooses <c>agent</c> (Active when chosen) and then suspends its user before returning —
    /// the interleaving of a status write landing between the selector's read and the offer.
    /// </summary>
    private sealed class SuspendingSelector(IUserStore users, Agent agent) : IAgentSelector
    {
        public async Task<EntityId?> SelectAgentAsync(
            TenantId tenantId, EntityId queueId, ChannelType channel, EntityId? preferredAgentId, CancellationToken ct)
        {
            var written = await users.UpdateAdminFieldsAsync(
                tenantId, agent.UserId, new AdminFieldsChange { Status = UserStatus.Suspended },
                DateTimeOffset.UtcNow, updatedBy: null, ct);
            written.Outcome.Should().Be(AdminFieldsWriteOutcome.Written);
            return agent.AgentId;
        }
    }
}

/// <summary>
/// licensed-agent-metering-follow-ups L17 (tasks.md 2.2 (b), design D3): an agent whose user is suspended after
/// the offer was made cannot accept it. Over the real pipeline: an ordinary access token is not looked up per
/// request, so the suspended user's still-valid token reaches <c>POST …/accept</c>.
/// </summary>
public sealed class AcceptAccountStatusEndpointTests : IClassFixture<ConversationOwnershipApiFactory>
{
    private readonly ConversationOwnershipApiFactory _factory;

    public AcceptAccountStatusEndpointTests(ConversationOwnershipApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Accept_ShouldReturn409AndKeepOfferAndReserveNothing_WhenUserSuspendedAfterOffer()
    {
        var (user, agent) = _factory.NewAgent("suspended-after-offer");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedOfferTo(agent, queue);
        using var client = _factory.ClientFor(user);
        using (var scope = _factory.Services.CreateScope())
        {
            var written = await scope.ServiceProvider.GetRequiredService<IUserStore>().UpdateAdminFieldsAsync(
                ConversationOwnershipApiFactory.Tenant, user.UserId, new AdminFieldsChange { Status = UserStatus.Suspended },
                DateTimeOffset.UtcNow, updatedBy: null, CancellationToken.None);
            written.Outcome.Should().Be(AdminFieldsWriteOutcome.Written);
        }

        var response = await client.PostAsync($"/api/v1/conversations/{conversation.ConversationId.Value}/accept", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "an agent whose user is not Active cannot accept an offer");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Agent account is not active.");
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.State.Should().Be(ConversationState.Offered);
        stored.Owner.Should().Be(ConversationOwner.ForQueue(queue.QueueId));
        _factory.LoadOf(agent.AgentId).Should().Be(0, because: "a refused accept reserves no capacity");
    }
}
