using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Core;
using H = Verbara.Platform.Api.Tests.SupervisorListen.ListenHarness;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen 2.7 (design D9, D12): the silent-listen routes on a host with no telephony — the
/// permission gate answers <c>not-permitted</c> with its code on every route, a bad destination answers
/// <c>invalid-destination</c>, and start, stop and current answer <c>voice-unavailable</c> (the host boots without AMI
/// and resolves no listen service). The listen endpoint is provisioned without AMI.
/// </summary>
public sealed class SupervisorListenEndpointsTests : IClassFixture<SupervisorListenApiFactory>
{
    private const string Refusals = "supervisor-listen-refusals.json";
    private const string ConversationId = "4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a";
    private readonly SupervisorListenApiFactory _factory;

    public SupervisorListenEndpointsTests(SupervisorListenApiFactory factory) => _factory = factory;

    private static async Task AssertRefusalAsync(HttpResponseMessage response, string key, string? conversationId)
    {
        var expected = SupervisorListenTestKit.FixtureBody(Refusals, key);
        expected["conversationId"] = conversationId;
        expected["listenId"] = null;
        ((int)response.StatusCode).Should().Be(SupervisorListenTestKit.FixtureStatus(Refusals, key));
        SupervisorListenTestKit.AssertSame(expected, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    [Fact]
    public async Task EveryListenRoute_ShouldRefuseNotPermitted_WithItsCode_WhenTheSupervisorLacksMonitor()
    {
        using var client = _factory.CreateSupervisorClient(permitted: false);

        await AssertRefusalAsync(await client.PostAsJsonAsync($"/api/v1/supervisor/conversations/{ConversationId}/listen",
            new { destination = "endpoint" }), "notPermitted", ConversationId);
        await AssertRefusalAsync(await client.PostAsync($"/api/v1/supervisor/conversations/{ConversationId}/listen/stop", null),
            "notPermitted", ConversationId);
        await AssertRefusalAsync(await client.GetAsync("/api/v1/supervisor/listen"), "notPermitted", null);
        await AssertRefusalAsync(await client.PostAsync("/api/v1/supervisor/listen-endpoint", null), "notPermitted", null);
        (await _factory.Services.GetRequiredService<Verbara.Platform.Queues.ISupervisorListenProfileStore>()
            .GetAsync(H.TenantId, EntityId.From(SupervisorListenApiFactory.UnpermittedUserId), CancellationToken.None))
            .Should().BeNull("nothing is provisioned for a caller without the permission");
    }

    [Theory]
    [InlineData("""{"destination":"desk"}""")]
    [InlineData("""{"phoneNumber":"+15550001111"}""")]
    [InlineData("{}")]
    public async Task Start_ShouldRefuseInvalidDestination(string json)
    {
        using var client = _factory.CreateSupervisorClient();

        var response = await client.PostAsync($"/api/v1/supervisor/conversations/{ConversationId}/listen",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        await AssertRefusalAsync(response, "invalidDestination", ConversationId);
    }

    [Fact]
    public async Task StartStopAndCurrent_ShouldAnswerVoiceUnavailable_OnAHostWithNoAmi()
    {
        using var client = _factory.CreateSupervisorClient();

        await AssertRefusalAsync(await client.PostAsJsonAsync($"/api/v1/supervisor/conversations/{ConversationId}/listen",
            new { destination = "endpoint" }), "voiceUnavailable", ConversationId);
        await AssertRefusalAsync(await client.PostAsync($"/api/v1/supervisor/conversations/{ConversationId}/listen/stop", null),
            "voiceUnavailable", ConversationId);
        await AssertRefusalAsync(await client.GetAsync("/api/v1/supervisor/listen"), "voiceUnavailable", null);
    }

    [Fact]
    public async Task ListenEndpoint_ShouldReturnTheCredentials_InTheFrozenShape_AndTheSameBodyLater()
    {
        using var client = _factory.CreateSupervisorClient();

        var first = await client.PostAsync("/api/v1/supervisor/listen-endpoint", null);
        var second = await client.PostAsync("/api/v1/supervisor/listen-endpoint", null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await first.Content.ReadAsStringAsync())!.AsObject();
        var fixture = SupervisorListenTestKit.FixtureBody("supervisor-sip-credentials.v1.json");
        body.Select(kv => kv.Key).Should().Equal(fixture.Select(kv => kv.Key));
        body["endpointId"]!.GetValue<string>().Should().Be($"acme-agent-sup-{SupervisorListenApiFactory.PermittedUserId}");
        body["authorizationUsername"]!.GetValue<string>().Should().MatchRegex("^acme-sl[0-9a-f]{8}$");
        body["listenCallerName"]!.GetValue<string>().Should().Be("Verbara Listen");
        (await second.Content.ReadAsStringAsync()).Should().Be(body.ToJsonString());
        _factory.EndpointProfiles.Created.Should().Be(1);
    }

    [Fact]
    public async Task TranscriptRoutes_ShouldStillAnswerAsBefore()
    {
        using var client = _factory.CreateSupervisorClient();

        var listen = await client.PostAsJsonAsync("/api/v1/supervisor/sessions/s-1/listen", new { supervisorId = "x" });
        var whisper = await client.PostAsJsonAsync("/api/v1/supervisor/sessions/s-1/whisper", new { text = "hi" });

        listen.StatusCode.Should().Be(HttpStatusCode.NotFound, "the Agent Assist transcript route is unchanged (no session)");
        whisper.StatusCode.Should().Be(HttpStatusCode.NotFound, "the text whisper route is unchanged (no session)");
    }
}

/// <summary>
/// supervisor-silent-listen 2.7 (design D11, D12): the listen routes against a listen service wired to fakes — the
/// frozen 200 bodies of start, current and stop, and nothing published on the event bus across them.
/// </summary>
public sealed class SupervisorListenVoiceEndpointsTests : IClassFixture<SupervisorListenVoiceApiFactory>
{
    private readonly SupervisorListenVoiceApiFactory _factory;

    public SupervisorListenVoiceEndpointsTests(SupervisorListenVoiceApiFactory factory) => _factory = factory;

    [Fact]
    public async Task StartCurrentStop_ShouldAnswerTheFrozenBodies_AndPublishNothing()
    {
        var events = new List<PlatformEvent>();
        using var subscription = _factory.Services.GetRequiredService<PlatformEventBus>().Events.Subscribe(events.Add);
        using var client = _factory.CreateSupervisorClient();
        await client.PostAsync("/api/v1/supervisor/listen-endpoint", null);
        var call = _factory.Harness.SeedVoiceCall();
        var id = call.ConversationId.Value;

        var start = await client.PostAsJsonAsync($"/api/v1/supervisor/conversations/{id}/listen", new { destination = "endpoint" });
        var current = await client.GetAsync("/api/v1/supervisor/listen");
        var stop = await client.PostAsync($"/api/v1/supervisor/conversations/{id}/listen/stop", null);
        var after = await client.GetAsync("/api/v1/supervisor/listen");

        start.StatusCode.Should().Be(HttpStatusCode.OK, await start.Content.ReadAsStringAsync());
        var startBody = JsonNode.Parse(await start.Content.ReadAsStringAsync())!.AsObject();
        var startFixture = SupervisorListenTestKit.FixtureBody("supervisor-listen-start.v1.json", "start");
        startBody.Select(kv => kv.Key).Should().Equal(startFixture.Select(kv => kv.Key));
        startBody["state"]!.GetValue<string>().Should().Be("dialing");
        startBody["listeningSince"].Should().BeNull();
        startBody.ContainsKey("listeningSince").Should().BeTrue();

        current.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await current.Content.ReadAsStringAsync())!["listenId"]!.GetValue<string>()
            .Should().Be(startBody["listenId"]!.GetValue<string>());

        stop.StatusCode.Should().Be(HttpStatusCode.OK);
        var stopBody = JsonNode.Parse(await stop.Content.ReadAsStringAsync())!.AsObject();
        stopBody.Select(kv => kv.Key).Should().Equal(
            SupervisorListenTestKit.FixtureBody("supervisor-listen-stop.v1.json").Select(kv => kv.Key));
        stopBody["endReason"]!.GetValue<string>().Should().Be("stopped");

        ((int)after.StatusCode).Should().Be(404);
        JsonNode.Parse(await after.Content.ReadAsStringAsync())!["error"]!.GetValue<string>()
            .Should().Be(SupervisorListenRefusal.ListenNotFound);

        events.Should().BeEmpty("a listen publishes nothing: no hub, SSE or event-bus event reaches the agent");
    }

    [Fact]
    public async Task Start_ShouldAnswerConversationNotFound_WhenTheIdIsWhitespace()
    {
        using var client = _factory.CreateSupervisorClient();

        var start = await client.PostAsJsonAsync("/api/v1/supervisor/conversations/%20/listen", new { destination = "endpoint" });

        ((int)start.StatusCode).Should().Be(404, await start.Content.ReadAsStringAsync());
        JsonNode.Parse(await start.Content.ReadAsStringAsync())!["error"]!.GetValue<string>()
            .Should().Be(SupervisorListenRefusal.ConversationNotFound);
    }
}
