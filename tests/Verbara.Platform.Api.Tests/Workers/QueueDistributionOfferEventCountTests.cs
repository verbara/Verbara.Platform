using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Auth;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.Workers;

/// <summary>
/// conversations-return-to-last-agent task 1.0 (design finding F1): one offer on the distribution path publishes
/// exactly one <see cref="ConversationOfferedEvent"/>. Drives the composed host's real
/// <see cref="ConversationSwitchboard"/> and a real <see cref="QueueDistributionWorker"/> over the in-memory stores
/// and one <see cref="PlatformEventBus"/>, so the count covers both publishers the path ever had.
/// </summary>
public sealed class QueueDistributionOfferEventCountTests : IClassFixture<AccountStatusApiFactory>
{
    private readonly AccountStatusApiFactory _factory;

    public QueueDistributionOfferEventCountTests(AccountStatusApiFactory factory) => _factory = factory;

    private IServiceProvider Services => _factory.Services;

    [Fact]
    public async Task DistributeAsync_ShouldPublishExactlyOneConversationOffered_WhenOneQueuedConversationIsOffered()
    {
        var tenant = new TenantId($"offer-count-{Guid.NewGuid():N}");
        var agent = await SeedAgentAsync(tenant);
        var queueId = EntityId.From($"offer-count-queue-{Guid.NewGuid():N}");
        var conversation = await SeedQueuedConversationAsync(tenant, queueId);
        var bus = Services.GetRequiredService<PlatformEventBus>();
        var offered = new List<ConversationOfferedEvent>();
        using var subscription = bus.Events.Subscribe(e =>
        {
            if (e is ConversationOfferedEvent o && o.ConversationId == conversation.ConversationId.Value)
                lock (offered)
                    offered.Add(o);
        });

        await NewWorker(tenant, new FixedSelector(agent.AgentId), bus).DistributeAsync(CancellationToken.None);

        var stored = await Services.GetRequiredService<IConversationStore>()
            .GetByIdAsync(tenant, conversation.ConversationId, CancellationToken.None);
        stored!.State.Should().Be(ConversationState.Offered);
        lock (offered)
        {
            offered.Should().ContainSingle(because: "one offer must reach the console as one conversation.offered");
            offered[0].QueueId.Should().Be(queueId.Value, because: "the event names the queue that owns the conversation");
            offered[0].AgentId.Should().Be(agent.AgentId.Value, because: "the event names the agent the selector chose");
        }
    }

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

    private async Task<Agent> SeedAgentAsync(TenantId tenant)
    {
        var id = Guid.NewGuid().ToString("N");
        var user = new User
        {
            UserId = EntityId.From($"offer-count-user-{id}"),
            TenantId = tenant,
            Email = $"offer-count-{id}@example.test",
            DisplayName = "Offer count",
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Services.GetRequiredService<IUserStore>().CreateAsync(user, CancellationToken.None);
        var agent = new Agent
        {
            AgentId = EntityId.From($"offer-count-agent-{id}"),
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
            ConversationId = EntityId.From($"offer-count-conv-{Guid.NewGuid():N}"),
            TenantId = tenant,
            ContactId = EntityId.From($"offer-count-contact-{Guid.NewGuid():N}"),
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Queued,
            Owner = ConversationOwner.ForQueue(queueId),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);
        return conversation;
    }

    /// <summary>The selector's choice is fixed: this test counts events, not selection.</summary>
    private sealed class FixedSelector(EntityId agentId) : IAgentSelector
    {
        public Task<EntityId?> SelectAgentAsync(
            TenantId tenantId, EntityId queueId, ChannelType channel, EntityId? preferredAgentId, CancellationToken ct) =>
            Task.FromResult<EntityId?>(agentId);
    }
}
