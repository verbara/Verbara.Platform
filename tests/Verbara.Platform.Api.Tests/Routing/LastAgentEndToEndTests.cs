using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Auth;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.Routing;

/// <summary>The composed host with <c>InboundRouting:LastAgent:Enabled = true</c>.</summary>
public sealed class LastAgentOnApiFactory : AccountStatusApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("InboundRouting:LastAgent:Enabled", "true");
    }
}

/// <summary>The composed host with the switch at its default (off) and no queue override.</summary>
public sealed class LastAgentOffApiFactory : AccountStatusApiFactory;

/// <summary>
/// conversations-return-to-last-agent task 3.1: the whole path in the composed host. A customer writes by WhatsApp,
/// agent A takes and closes the conversation, the same customer writes again; the host's webhook processor routes the
/// new conversation through the host's router chain, and one <see cref="QueueDistributionWorker.DistributeAsync"/>
/// with the host's real <see cref="IAgentSelector"/> (round robin over A and B, both <c>Available</c>) offers it.
/// The metadata key names and values and the counter names are the fixtures' verbatim
/// (<c>conversation-routing-preference.v1.json</c>, <c>last-agent-routing-metric.v1.json</c>).
/// </summary>
public sealed class LastAgentEndToEndTests : IClassFixture<LastAgentOnApiFactory>, IClassFixture<LastAgentOffApiFactory>
{
    private readonly LastAgentOnApiFactory _on;
    private readonly LastAgentOffApiFactory _off;

    public LastAgentEndToEndTests(LastAgentOnApiFactory on, LastAgentOffApiFactory off)
    {
        _on = on;
        _off = off;
    }

    [Fact]
    public async Task ReturningCustomer_ShouldBeOfferedFirstToTheLastAgent_WhenTheHostSwitchIsOn()
    {
        await using var scenario = await Scenario.StartAsync(_on.Services);

        var (returned, preferredArgument) = await scenario.CloseAsAThenReturnAndDistributeAsync();

        returned.QueuedMetadata.Should().Contain("_preferredAgentId", scenario.AgentA.Value,
            "the route stamped the last owner before the first offer");
        preferredArgument.Should().Be(scenario.AgentA, "the worker passed the stamped preference to the real selector");
        returned.Offered.State.Should().Be(ConversationState.Offered);
        returned.Offered.Metadata.Should().Contain("_offeredTo", scenario.AgentA.Value);
        returned.Offered.Metadata.Should().NotContainKey("_preferredAgentId", "the first offer consumed the preference");
        returned.Offered.Metadata.Should().Contain("_preferredAgentOutcome", "offered-preferred");
        scenario.Outcomes.Should().Equal("no-history", "stamped", "offered-preferred");
    }

    [Fact]
    public async Task ReturningCustomer_ShouldRouteWithNoPreference_WhenTheHostSwitchIsOff()
    {
        await using var scenario = await Scenario.StartAsync(_off.Services);

        var (returned, preferredArgument) = await scenario.CloseAsAThenReturnAndDistributeAsync();

        returned.QueuedMetadata.Should().NotContainKey("_preferredAgentId");
        preferredArgument.Should().BeNull("with the switch off the worker has no preference to pass");
        returned.Offered.State.Should().Be(ConversationState.Offered);
        returned.Offered.Metadata.Should().NotContainKey("_preferredAgentId");
        returned.Offered.Metadata.Should().NotContainKey("_preferredAgentOutcome");
        scenario.Outcomes.Should().Equal("disabled", "disabled");
    }

    private sealed record Returned(IReadOnlyDictionary<string, string> QueuedMetadata, Conversation Offered);

    private sealed class Scenario : IAsyncDisposable
    {
        private const string Phone = "5215550003131";

        private readonly IServiceProvider _services;
        private readonly MeterListener _listener = new();
        private readonly List<string> _outcomes = [];
        private readonly TenantId _tenant = new($"la-e2e-{Guid.NewGuid():N}");

        private Scenario(IServiceProvider services) => _services = services;

        public EntityId AgentA { get; private set; }

        public EntityId AgentB { get; private set; }

        public EntityId QueueId { get; private set; }

        public IReadOnlyList<string> Outcomes
        {
            get
            {
                lock (_outcomes) return [.. _outcomes];
            }
        }

        public static async Task<Scenario> StartAsync(IServiceProvider services)
        {
            var scenario = new Scenario(services);
            await scenario.SeedAsync();
            return scenario;
        }

        public ValueTask DisposeAsync()
        {
            _listener.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task SeedAsync()
        {
            // The counter of this host, by the fixture's meter and instrument names.
            var hostMetrics = _services.GetRequiredService<RoutingMetrics>();
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, hostMetrics.LastAgent.Meter)
                    && instrument.Meter.Name == "verbara.platform.routing" && instrument.Name == "routing.last_agent")
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "outcome")
                        lock (_outcomes) _outcomes.Add((string)tag.Value!);
                }
            });
            _listener.Start();

            var queue = new Queue
            {
                QueueId = EntityId.From($"la-e2e-queue-{Guid.NewGuid():N}"),
                TenantId = _tenant,
                Name = "Returning customers",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await _services.GetRequiredService<IQueueStore>().SaveAsync(queue, CancellationToken.None);
            QueueId = queue.QueueId;
            AgentA = await SeedAgentAsync("a");
            AgentB = await SeedAgentAsync("b");
        }

        private async Task<EntityId> SeedAgentAsync(string name)
        {
            var id = $"la-e2e-{name}-{Guid.NewGuid():N}";
            var user = new User
            {
                UserId = EntityId.From($"user-{id}"),
                TenantId = _tenant,
                Email = $"{id}@example.test",
                DisplayName = $"Agent {name.ToUpperInvariant()}",
                Role = UserRole.Agent,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await _services.GetRequiredService<IUserStore>().CreateAsync(user, CancellationToken.None);
            var agent = new Agent
            {
                AgentId = EntityId.From($"agent-{id}"),
                TenantId = _tenant,
                UserId = user.UserId,
                DisplayName = user.DisplayName,
                State = AgentState.Available,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await _services.GetRequiredService<IAgentStore>().SaveAsync(agent, CancellationToken.None);
            await _services.GetRequiredService<IQueueMembershipStore>().SaveAsync(new QueueMembership
            {
                TenantId = _tenant,
                QueueId = QueueId,
                AgentId = agent.AgentId,
                Source = MembershipSource.Manual,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
            return agent.AgentId;
        }

        public async Task<(Returned Returned, EntityId? PreferredArgument)> CloseAsAThenReturnAndDistributeAsync()
        {
            var store = _services.GetRequiredService<IConversationStore>();
            var switchboard = _services.GetRequiredService<IConversationSwitchboard>();

            // First contact: routed by the host's chain, then offered to, accepted by and closed by A.
            var first = await InboundAsync("hola");
            (await switchboard.OfferToAgentAsync(first, _tenant, AgentA, CancellationToken.None)).Success.Should().BeTrue();
            (await switchboard.AcceptAsync(first, _tenant, AgentA, CancellationToken.None)).Success.Should().BeTrue();
            (await switchboard.WrapUpAsync(_tenant, first, AgentA, CancellationToken.None)).Success.Should().BeTrue();
            (await store.TryConditionalCloseWrapUpAsync(_tenant, first, DateTimeOffset.UtcNow, CancellationToken.None))
                .Should().BeTrue();

            // The return: a new conversation of the same contact.
            var second = await InboundAsync("vuelvo");
            second.Should().NotBe(first, "a message after the close opens a new conversation");
            var queued = (await store.GetByIdAsync(_tenant, second, CancellationToken.None))!;
            queued.State.Should().Be(ConversationState.Queued);
            queued.Owner.Should().Be(ConversationOwner.ForQueue(QueueId));
            var queuedMetadata = new Dictionary<string, string>(queued.Metadata);

            var selector = new RecordingSelector(_services.GetRequiredService<IAgentSelector>());
            var tenants = Substitute.For<ITenantStore>();
            tenants.GetAllActiveAsync(Arg.Any<CancellationToken>())
                .Returns([new Tenant { TenantId = _tenant.Value, Name = _tenant.Value, Status = TenantStatus.Active }]);
            using (var worker = new QueueDistributionWorker(
                       store, tenants, selector, switchboard, _services.GetRequiredService<PlatformEventBus>(),
                       _services.GetRequiredService<IClock>(), new ServiceHeartbeat(), Options.Create(new DistributionOptions()),
                       NullLogger<QueueDistributionWorker>.Instance))
            {
                await worker.DistributeAsync(CancellationToken.None);
            }

            selector.Calls.Should().ContainSingle("one queued conversation, one selection");
            var offered = (await store.GetByIdAsync(_tenant, second, CancellationToken.None))!;
            return (new Returned(queuedMetadata, offered), selector.Calls[0]);
        }

        private async Task<EntityId> InboundAsync(string text)
        {
            var inbound = new InboundMessage(
                new ChannelAddress(ChannelType.WhatsApp, Phone),
                new MessageEnvelope([new TextBlock(text)]),
                "wamid.la-e2e." + Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow);
            using (var scope = _services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<WebhookInboundProcessor>()
                    .ProcessAsync(_tenant, ChannelType.WhatsApp, WebhookResult.From([inbound], []), CancellationToken.None);
            }

            var page = await _services.GetRequiredService<IConversationStore>()
                .ListAsync(_tenant, new ConversationQuery { PageSize = 50 }, CancellationToken.None);
            return page.Items.OrderByDescending(c => c.CreatedAt).First().ConversationId;
        }
    }

    /// <summary>The host's selector, recording the preference each call receives.</summary>
    private sealed class RecordingSelector(IAgentSelector inner) : IAgentSelector
    {
        public List<EntityId?> Calls { get; } = [];

        public Task<EntityId?> SelectAgentAsync(
            TenantId tenantId, EntityId queueId, ChannelType channel, EntityId? preferredAgentId, CancellationToken ct)
        {
            Calls.Add(preferredAgentId);
            return inner.SelectAgentAsync(tenantId, queueId, channel, preferredAgentId, ct);
        }
    }
}
