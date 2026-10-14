using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.OutboundTrunk;
using Verbara.Platform.Core;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Enums;

namespace Verbara.Platform.Api.Tests.Voice;

/// <summary>
/// readiness-without-asterisk (design D5; spec "Voice action routes answer 503 with Retry-After while AMI is down"):
/// <c>POST /api/v1/conversations/{id}/voice-transfer</c> and <c>POST /api/v1/voice/dial</c> answer 503 with
/// <c>Retry-After: 30</c> and the route's typed <c>voice-unavailable</c> body while this pod's primary AMI connection is
/// not <c>Connected</c>, before the handler runs, and when the handler raises <see cref="AmiNotConnectedException"/>.
/// The voice connection seam is a substitute in DI (<see cref="VoiceEndpointsTrunkNotDialableTests.Factory"/>), and so
/// are the dial and transfer services: each one models what the real service answers in that state, so the same test
/// shows today's answer when the gate is missing.
/// </summary>
public sealed class VoiceActionRouteGateTests : IClassFixture<VoiceEndpointsTrunkNotDialableTests.Factory>
{
    private const string TransferTarget = "+15559998888";
    private const string DialNumber = "+573001234567";
    private const string VoiceUnavailableBody = """{"accepted":false,"error":"voice-unavailable","trunkId":null}""";

    private readonly VoiceEndpointsTrunkNotDialableTests.Factory _factory;

    public VoiceActionRouteGateTests(VoiceEndpointsTrunkNotDialableTests.Factory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    /// <summary>Every <see cref="AmiConnectionState"/> but <c>Connected</c>, so a state the Sdk adds is covered too.</summary>
    public static TheoryData<AmiConnectionState> NotConnectedStates() =>
        new(Enum.GetValues<AmiConnectionState>().Where(s => s != AmiConnectionState.Connected));

    private async Task<HttpResponseMessage> TransferAsync(string conversationId)
    {
        using var client = _factory.CreateSupervisorClient();
        return await client.PostAsJsonAsync($"/api/v1/conversations/{conversationId}/voice-transfer",
            new { kind = "external", target = TransferTarget });
    }

    private async Task<HttpResponseMessage> DialAsync()
    {
        using var client = _factory.CreateSupervisorClient();
        return await client.PostAsJsonAsync("/api/v1/voice/dial", new { toNumber = DialNumber });
    }

    internal static async Task AssertVoiceUnavailableAsync(HttpResponseMessage response, string because)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "{0}; body: {1}", because, body);
        response.Headers.RetryAfter.Should().NotBeNull("a 503 voice-unavailable carries Retry-After");
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
        body.Should().Be(VoiceUnavailableBody);
    }

    [Theory]
    [MemberData(nameof(NotConnectedStates))]
    public async Task Transfer_ShouldAnswer503VoiceUnavailableAndSendNothing_WhenThePrimaryAmiConnectionIsNotConnected(
        AmiConnectionState state)
    {
        _factory.VoiceConnection = new VoiceConnectionSnapshot(true, state);
        // What the real service does past its checks while AMI is down: the Sdk send throws.
        _factory.CallControl.Failure = new AmiNotConnectedException();
        var conversationId = _factory.SeedOwnedCall();

        var response = await TransferAsync(conversationId);

        await AssertVoiceUnavailableAsync(response, $"a transfer while the primary AMI connection is {state} is unavailable");
        _factory.CallControl.Calls.Should().Be(0, "the gate answers before the handler, so no AMI action is sent");
    }

    [Theory]
    [MemberData(nameof(NotConnectedStates))]
    public async Task Dial_ShouldAnswer503VoiceUnavailableAndSendNothing_WhenThePrimaryAmiConnectionIsNotConnected(
        AmiConnectionState state)
    {
        _factory.VoiceConnection = new VoiceConnectionSnapshot(true, state);
        // What the real service answers when the Originate fails: it catches and refuses.
        _factory.Dial.Outcome = new VoiceDialOutcome(false, null, "originate-failed");

        var response = await DialAsync();

        await AssertVoiceUnavailableAsync(response, $"a dial while the primary AMI connection is {state} is unavailable");
        _factory.Dial.Calls.Should().Be(0, "the gate answers before the handler, so no Originate is sent");
    }

    [Fact]
    public async Task Transfer_ShouldAnswer503VoiceUnavailable_WhenThePoolHoldsNoPrimaryServer()
    {
        _factory.VoiceConnection = new VoiceConnectionSnapshot(true, null);
        // What the real service answers with no primary server (VoiceCallControlService: ami-unavailable, mapped to 400).
        _factory.CallControl.Outcome = new VoiceTransferOutcome(false, "ami-unavailable");
        var conversationId = _factory.SeedOwnedCall();

        var response = await TransferAsync(conversationId);

        await AssertVoiceUnavailableAsync(response, "a pod with no primary server cannot transfer");
        _factory.CallControl.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Dial_ShouldAnswer503VoiceUnavailable_WhenThePoolHoldsNoPrimaryServer()
    {
        _factory.VoiceConnection = new VoiceConnectionSnapshot(true, null);
        _factory.Dial.Outcome = new VoiceDialOutcome(false, null, "originate-failed");

        var response = await DialAsync();

        await AssertVoiceUnavailableAsync(response, "a pod with no primary server cannot dial");
        _factory.Dial.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Transfer_ShouldAnswer503VoiceUnavailable_WhenTheAmiConnectionDropsWhileTheRequestIsInFlight()
    {
        _factory.CallControl.Failure = new AmiNotConnectedException();
        var conversationId = _factory.SeedOwnedCall();

        var response = await TransferAsync(conversationId);

        await AssertVoiceUnavailableAsync(response, "a not-connected failure in flight is unavailable, not a server bug");
        _factory.CallControl.Calls.Should().Be(1, "the connection was up when the request arrived");
    }

    [Fact]
    public async Task Dial_ShouldAnswer503VoiceUnavailable_WhenTheAmiConnectionDropsWhileTheRequestIsInFlight()
    {
        _factory.Dial.Failure = new AmiNotConnectedException();

        var response = await DialAsync();

        await AssertVoiceUnavailableAsync(response, "a not-connected failure in flight is unavailable, not a server bug");
        _factory.Dial.Calls.Should().Be(1, "the connection was up when the request arrived");
    }

    [Fact]
    public async Task Transfer_ShouldLeaveOtherFailuresToTheErrorMiddleware_WhenConnected()
    {
        _factory.CallControl.Failure = new InvalidOperationException("not an AMI connection failure");
        var conversationId = _factory.SeedOwnedCall();

        var response = await TransferAsync(conversationId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "only a not-connected failure becomes voice-unavailable");
        response.Headers.RetryAfter.Should().BeNull();
        (await response.Content.ReadAsStringAsync()).Should().NotContain("voice-unavailable");
    }

    [Fact]
    public async Task Transfer_ShouldAnswer403NotOwner_WhenConnectedAndTheCallerDoesNotOwnTheCall()
    {
        var conversationId = _factory.SeedCall(EntityId.New());

        var response = await TransferAsync(conversationId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Be("""{"accepted":false,"error":"not-owner","trunkId":null}""");
        _factory.CallControl.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Transfer_ShouldAnswer503BeforeTheOwnerCheck_WhenNotConnectedAndTheCallerDoesNotOwnTheCall()
    {
        _factory.VoiceConnection = new VoiceConnectionSnapshot(true, AmiConnectionState.Reconnecting);
        var conversationId = _factory.SeedCall(EntityId.New());

        var response = await TransferAsync(conversationId);

        await AssertVoiceUnavailableAsync(response, "availability is answered before the resource-level check (design D5)");
    }
}

/// <summary>
/// readiness-without-asterisk D5 through the host's real voice connection seam, which the gate resolves from DI: AMI is
/// configured and the pool holds no primary server, so both routes answer 503 <c>voice-unavailable</c>.
/// </summary>
public sealed class VoiceActionRouteGateRealSeamTests : IClassFixture<VoiceEndpointsTrunkNotDialableTests.RealVoiceConnectionFactory>
{
    private readonly VoiceEndpointsTrunkNotDialableTests.RealVoiceConnectionFactory _factory;

    public VoiceActionRouteGateRealSeamTests(VoiceEndpointsTrunkNotDialableTests.RealVoiceConnectionFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    [Fact]
    public async Task VoiceRoutes_ShouldAnswer503VoiceUnavailable_WhenTheRegisteredSeamReadsNoPrimaryServer()
    {
        _factory.Services.GetRequiredService<IVoiceConnectionStatus>().Read().StateName
            .Should().Be(VoiceConnectionSnapshot.NoPrimaryServer, "the stubbed hosted services never seed the pool");
        _factory.CallControl.Outcome = new VoiceTransferOutcome(false, "ami-unavailable");
        _factory.Dial.Outcome = new VoiceDialOutcome(false, null, "originate-failed");
        var conversationId = _factory.SeedOwnedCall();
        using var client = _factory.CreateSupervisorClient();

        var transfer = await client.PostAsJsonAsync($"/api/v1/conversations/{conversationId}/voice-transfer",
            new { kind = "external", target = "+15559998888" });
        var dial = await client.PostAsJsonAsync("/api/v1/voice/dial", new { toNumber = "+573001234567" });

        await VoiceActionRouteGateTests.AssertVoiceUnavailableAsync(transfer, "the real seam reads no primary server");
        await VoiceActionRouteGateTests.AssertVoiceUnavailableAsync(dial, "the real seam reads no primary server");
        _factory.CallControl.Calls.Should().Be(0);
        _factory.Dial.Calls.Should().Be(0);
    }
}

/// <summary>
/// readiness-without-asterisk D5, the routes the gate leaves alone: voice configuration stored in Postgres answers the
/// same while the primary AMI connection is down as while it is up. An administrator of the Customer test tenant, on
/// a host with AMI configured and the voice connection seam substituted.
/// </summary>
public sealed class VoiceConfigurationRoutesUngatedTests : IClassFixture<VoiceConfigurationRoutesUngatedTests.Factory>
{
    private readonly Factory _factory;

    public VoiceConfigurationRoutesUngatedTests(Factory factory) => _factory = factory;

    private async Task<(HttpResponseMessage Connected, string ConnectedBody, HttpResponseMessage Down, string DownBody)> ReadBothWaysAsync(
        HttpClient admin, string path)
    {
        _factory.VoiceConnection = VoiceEndpointsTrunkNotDialableTests.Factory.Connected;
        var connected = await admin.GetAsync(path);
        _factory.VoiceConnection = new VoiceConnectionSnapshot(true, AmiConnectionState.Reconnecting);
        var down = await admin.GetAsync(path);
        _factory.VoiceConnection = VoiceEndpointsTrunkNotDialableTests.Factory.Connected;
        return (connected, await connected.Content.ReadAsStringAsync(), down, await down.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TrunkList_ShouldAnswerAsWithAmiConnected_WhenThePrimaryAmiConnectionIsNotConnected()
    {
        using var admin = _factory.CreateAuthenticatedClient();
        using var body = new StringContent(
            """
            {"name":"carrier-gate-probe","displayName":null,"type":"pjsip","isActive":true,"maxChannels":10,
             "transport":"transport-udp","codecs":"ulaw,alaw","authUsername":null,"authPassword":null,
             "registrationUri":null,"clientUri":null,"context":"from-trunk","matchHost":null}
            """,
            System.Text.Encoding.UTF8,
            "application/json");
        var create = await admin.PostAsync("/api/v1/admin/trunks", body);
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var (connected, connectedBody, down, downBody) = await ReadBothWaysAsync(admin, "/api/v1/admin/trunks");

        connected.StatusCode.Should().Be(HttpStatusCode.OK);
        connectedBody.Should().Contain("carrier-gate-probe");
        down.StatusCode.Should().Be(HttpStatusCode.OK, "a trunk list needs no AMI action; body: {0}", downBody);
        down.Headers.RetryAfter.Should().BeNull();
        downBody.Should().Be(connectedBody);
    }

    [Fact]
    public async Task DidRouteList_ShouldAnswerAsWithAmiConnected_WhenThePrimaryAmiConnectionIsNotConnected()
    {
        using var admin = _factory.CreateAuthenticatedClient();

        var (connected, connectedBody, down, downBody) = await ReadBothWaysAsync(admin, "/api/v1/admin/did-routes");

        connected.StatusCode.Should().Be(HttpStatusCode.OK);
        down.StatusCode.Should().Be(HttpStatusCode.OK, "a DID route list needs no AMI action; body: {0}", downBody);
        down.Headers.RetryAfter.Should().BeNull();
        downBody.Should().Be(connectedBody);
    }

    /// <summary>The authenticated admin host with an AMI host name (nothing connects to it) and a substituted seam.</summary>
    public sealed class Factory : AuthenticatedPlatformApiFactory
    {
        internal VoiceConnectionSnapshot VoiceConnection { get; set; } = VoiceEndpointsTrunkNotDialableTests.Factory.Connected;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Asterisk:Ami:Hostname", "ami.invalid");
            builder.ConfigureTestServices(services =>
            {
                var voice = Substitute.For<IVoiceConnectionStatus>();
                voice.Read().Returns(_ => VoiceConnection);
                services.RemoveAll<IVoiceConnectionStatus>();
                services.AddSingleton(voice);
            });
        }
    }
}
