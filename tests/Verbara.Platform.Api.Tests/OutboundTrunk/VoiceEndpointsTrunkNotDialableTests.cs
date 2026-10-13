using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.SupervisorListen;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

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
    /// tenant <c>acme</c>, and whose dial and transfer services are substitutes.
    /// </summary>
    public sealed class Factory : SupervisorListenApiFactory
    {
        private static readonly EntityId AgentId = EntityId.From("a6e1c0b2d3f44e5f8a9b0c1d2e3f4a5b");

        internal FakeDial Dial { get; } = new();
        internal FakeCallControl CallControl { get; } = new();
        internal IConversationStore Conversations { get; } = Substitute.For<IConversationStore>();

        /// <summary>A voice conversation owned by the caller's agent; returns its id.</summary>
        internal string SeedOwnedCall()
        {
            var conversation = new Conversation
            {
                ConversationId = EntityId.New(),
                TenantId = ListenHarness.TenantId,
                ContactId = EntityId.New(),
                Channel = ChannelType.Voice,
                State = ConversationState.Active,
                Owner = new ConversationOwner(ConversationOwnerKind.Agent, AgentId),
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
            });
        }
    }

    internal sealed class FakeDial : IAgentOutboundDialService
    {
        public VoiceDialOutcome Outcome { get; set; } = new(false, null, "unset");

        public Task<VoiceDialOutcome> DialAsync(
            TenantId tenantId, EntityId agentId, string toNumber, EntityId? contactId, CancellationToken ct) =>
            Task.FromResult(Outcome);
    }

    internal sealed class FakeCallControl : IVoiceCallControlService
    {
        public VoiceTransferOutcome Outcome { get; set; } = new(false, "unset");

        public Task<VoiceTransferOutcome> BlindTransferAsync(
            TenantId tenantId, EntityId conversationId, VoiceTransferTarget target, CancellationToken ct) =>
            Task.FromResult(Outcome);
    }
}
