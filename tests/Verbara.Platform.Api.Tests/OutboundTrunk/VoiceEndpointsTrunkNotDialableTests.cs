using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.SupervisorListen;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Enums;

namespace Verbara.Platform.Api.Tests.OutboundTrunk;

/// <summary>
/// outbound-calls-reach-the-trunk (design D4): <c>POST /api/v1/voice/dial</c> and
/// <c>POST /api/v1/conversations/{id}/voice-transfer</c> answer a <c>trunk-not-dialable</c> outcome with 409 and the
/// frozen body of <c>voice-dial-transfer.v1.json</c>, and an accepted call with 200 and <c>"trunkId": null</c>.
/// The dial and transfer services are substitutes; the resolution itself is covered by the service tests.
/// </summary>
public sealed class VoiceEndpointsTrunkNotDialableTests : IClassFixture<VoiceEndpointsTrunkNotDialableTests.Factory>
{
    private const string CorrelationId = "7c2e9a10b4d34f6e8a1b2c3d4e5f6a7b";
    private readonly Factory _factory;

    public VoiceEndpointsTrunkNotDialableTests(Factory factory) => _factory = factory;

    private static async Task AssertBodyAsync(HttpResponseMessage response, string key)
    {
        var entry = VoiceDialTransferContractTests.FixtureEntry(key);
        ((int)response.StatusCode).Should().Be(entry["status"]!.GetValue<int>(), key);
        SupervisorListenTestKit.AssertSame(entry["body"]!.AsObject(), JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    [Fact]
    public async Task Dial_ShouldAnswer409WithTheTrunkId_WhenTheTrunkIsNotDialable()
    {
        _factory.Dial.Outcome = new VoiceDialOutcome(false, null, "trunk-not-dialable", 4);
        using var client = _factory.CreateSupervisorClient();

        var response = await client.PostAsJsonAsync("/api/v1/voice/dial", new { toNumber = "+573001234567" });

        await AssertBodyAsync(response, "dialTrunkNotDialable");
    }

    [Fact]
    public async Task Dial_ShouldAnswer200WithANullTrunkId_WhenAccepted()
    {
        _factory.Dial.Outcome = new VoiceDialOutcome(true, CorrelationId, null);
        using var client = _factory.CreateSupervisorClient();

        var response = await client.PostAsJsonAsync("/api/v1/voice/dial", new { toNumber = "+573001234567" });

        await AssertBodyAsync(response, "dialAccepted");
    }

    [Fact]
    public async Task Dial_ShouldKeepItsOtherRefusalsAt400()
    {
        _factory.Dial.Outcome = new VoiceDialOutcome(false, null, "no-trunk");
        using var client = _factory.CreateSupervisorClient();

        var response = await client.PostAsJsonAsync("/api/v1/voice/dial", new { toNumber = "+573001234567" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Be("""{"accepted":false,"error":"no-trunk","trunkId":null}""");
    }

    [Fact]
    public async Task Transfer_ShouldAnswer409WithTheTrunkId_WhenTheTrunkIsNotDialable()
    {
        var conversationId = _factory.SeedOwnedCall();
        _factory.CallControl.Outcome = new VoiceTransferOutcome(false, "trunk-not-dialable", 5);
        using var client = _factory.CreateSupervisorClient();

        var response = await client.PostAsJsonAsync($"/api/v1/conversations/{conversationId}/voice-transfer",
            new { kind = "external", target = "+15559998888" });

        await AssertBodyAsync(response, "transferTrunkNotDialable");
    }

    [Fact]
    public async Task Transfer_ShouldAnswer200WithANullTrunkId_WhenAccepted()
    {
        var conversationId = _factory.SeedOwnedCall();
        _factory.CallControl.Outcome = new VoiceTransferOutcome(true, null);
        using var client = _factory.CreateSupervisorClient();

        var response = await client.PostAsJsonAsync($"/api/v1/conversations/{conversationId}/voice-transfer",
            new { kind = "external", target = "+15559998888" });

        await AssertBodyAsync(response, "transferAccepted");
    }

    /// <summary>
    /// A host with the voice routes mapped (an AMI host name; nothing connects to it), whose caller is an agent of
    /// tenant <c>acme</c>, and whose dial and transfer services are substitutes. The voice connection seam
    /// (readiness-without-asterisk D5) is a substitute that reads <see cref="VoiceConnection"/>, <c>Connected</c> by
    /// default, so the route gate lets these requests through; <see cref="RealVoiceConnectionFactory"/> keeps the
    /// host's real seam instead.
    /// </summary>
    public class Factory : SupervisorListenApiFactory
    {
        /// <summary>The caller's agent: the owner of <see cref="SeedOwnedCall"/>.</summary>
        internal static readonly EntityId AgentId = EntityId.From("a6e1c0b2d3f44e5f8a9b0c1d2e3f4a5b");

        /// <summary>AMI configured, the primary connection <c>Connected</c>.</summary>
        internal static readonly VoiceConnectionSnapshot Connected = new(true, AmiConnectionState.Connected);

        internal FakeDial Dial { get; } = new();
        internal FakeCallControl CallControl { get; } = new();
        internal IConversationStore Conversations { get; } = Substitute.For<IConversationStore>();

        /// <summary>What the substituted voice connection seam reads on every request.</summary>
        internal VoiceConnectionSnapshot VoiceConnection { get; set; } = Connected;

        /// <summary>Whether the host's voice connection seam is replaced by the substitute that reads <see cref="VoiceConnection"/>.</summary>
        protected virtual bool SubstituteVoiceConnection => true;

        /// <summary>Back to the defaults: <c>Connected</c>, fakes unset, no recorded call.</summary>
        internal void Reset()
        {
            VoiceConnection = Connected;
            Dial.Reset();
            CallControl.Reset();
        }

        /// <summary>A voice conversation owned by the caller's agent; returns its id.</summary>
        internal string SeedOwnedCall() => SeedCall(AgentId);

        /// <summary>A live voice conversation owned by the agent <paramref name="owner"/>; returns its id.</summary>
        internal string SeedCall(EntityId owner)
        {
            var conversation = new Conversation
            {
                ConversationId = EntityId.New(),
                TenantId = ListenHarness.TenantId,
                ContactId = EntityId.New(),
                Channel = ChannelType.Voice,
                State = ConversationState.Active,
                Owner = new ConversationOwner(ConversationOwnerKind.Agent, owner),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            Conversations.GetByIdAsync(ListenHarness.TenantId, conversation.ConversationId, Arg.Any<CancellationToken>())
                .Returns(conversation);
            return conversation.ConversationId.Value;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Asterisk:Ami:Hostname", "ami.invalid");
            builder.ConfigureTestServices(services =>
            {
                var agents = Substitute.For<IAgentStore>();
                agents.GetByUserIdAsync(ListenHarness.TenantId, ListenHarness.Supervisor, Arg.Any<CancellationToken>())
                    .Returns(new Agent
                    {
                        AgentId = AgentId, TenantId = ListenHarness.TenantId, UserId = ListenHarness.Supervisor,
                        DisplayName = "Ana", State = AgentState.Available, CreatedAt = DateTimeOffset.UtcNow,
                    });
                services.RemoveAll<IAgentStore>();
                services.AddSingleton(agents);
                services.RemoveAll<IConversationStore>();
                services.AddSingleton(Conversations);
                services.RemoveAll<IAgentOutboundDialService>();
                services.AddSingleton<IAgentOutboundDialService>(Dial);
                services.RemoveAll<IVoiceCallControlService>();
                services.AddSingleton<IVoiceCallControlService>(CallControl);
                if (SubstituteVoiceConnection)
                {
                    var voice = Substitute.For<IVoiceConnectionStatus>();
                    voice.Read().Returns(_ => VoiceConnection);
                    services.RemoveAll<IVoiceConnectionStatus>();
                    services.AddSingleton(voice);
                }
            });
        }
    }

    /// <summary>
    /// <see cref="Factory"/> with the host's real voice connection seam: AMI is configured, and the pool holds no
    /// primary server, because the stubbed hosted services never seed it.
    /// </summary>
    public sealed class RealVoiceConnectionFactory : Factory
    {
        protected override bool SubstituteVoiceConnection => false;
    }

    /// <summary>A dial service that answers <see cref="Outcome"/>, or throws <see cref="Failure"/>, and counts its calls.</summary>
    internal sealed class FakeDial : IAgentOutboundDialService
    {
        private int _calls;

        public VoiceDialOutcome Outcome { get; set; } = new(false, null, "unset");

        /// <summary>When set, <see cref="DialAsync"/> throws it instead of answering.</summary>
        public Exception? Failure { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public void Reset()
        {
            Outcome = new(false, null, "unset");
            Failure = null;
            Interlocked.Exchange(ref _calls, 0);
        }

        public Task<VoiceDialOutcome> DialAsync(
            TenantId tenantId, EntityId agentId, string toNumber, EntityId? contactId, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Failure is null ? Task.FromResult(Outcome) : Task.FromException<VoiceDialOutcome>(Failure);
        }
    }

    /// <summary>A call-control service that answers <see cref="Outcome"/>, or throws <see cref="Failure"/>, and counts its calls.</summary>
    internal sealed class FakeCallControl : IVoiceCallControlService
    {
        private int _calls;

        public VoiceTransferOutcome Outcome { get; set; } = new(false, "unset");

        /// <summary>When set, <see cref="BlindTransferAsync"/> throws it instead of answering.</summary>
        public Exception? Failure { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public void Reset()
        {
            Outcome = new(false, "unset");
            Failure = null;
            Interlocked.Exchange(ref _calls, 0);
        }

        public Task<VoiceTransferOutcome> BlindTransferAsync(
            TenantId tenantId, EntityId conversationId, VoiceTransferTarget target, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Failure is null ? Task.FromResult(Outcome) : Task.FromException<VoiceTransferOutcome>(Failure);
        }
    }
}
