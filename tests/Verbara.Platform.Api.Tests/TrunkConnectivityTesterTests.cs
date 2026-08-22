using System.Text.Json;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// P2 trunk connectivity test. Verifies the leader gate, tenant-scoped trunk resolution, the auth-mode
/// branch (register vs IP-ACL), and tolerant parsing of canned <c>pjsip show ...</c> CLI output fed
/// through a stubbed AMI <see cref="CommandResponse"/> (its <c>.Output</c> reads RawFields["__CommandOutput"]).
/// </summary>
public sealed class TrunkConnectivityTesterTests : IDisposable
{
    private const string Tenant = "acme";
    private static readonly TenantId TenantId = new(Tenant);

    private readonly TrunkStoreBase _trunks = Substitute.For<TrunkStoreBase>();
    private readonly IAmiConnection _ami = Substitute.For<IAmiConnection>();
    private readonly VerbaraServerPool _serverPool =
        new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);

    public void Dispose() => _serverPool.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Builds a <see cref="CommandResponse"/> whose <c>.Output</c> returns <paramref name="output"/>.</summary>
    private static CommandResponse CommandResponseWith(string output) =>
        new()
        {
            Response = "Success",
            RawFields = new Dictionary<string, string> { ["__CommandOutput"] = output },
        };

    /// <summary>
    /// Stubs the AMI command response keyed by a substring of the command (e.g. "show endpoint",
    /// "show registrations", "show identify") so each query gets its own canned CLI output.
    /// </summary>
    private void StubCommand(string commandContains, string output) =>
        _ami.SendActionAsync<CommandResponse>(
                Arg.Is<CommandAction>(a => a != null && a.Command != null && a.Command.Contains(commandContains)),
                Arg.Any<CancellationToken>())
            .Returns(CommandResponseWith(output));

    private TrunkConnectivityTester CreateService(bool isLeader = true, bool withServer = true)
    {
        if (withServer)
            _serverPool.AddExistingServer("primary", new VerbaraServer(_ami, NullLogger<VerbaraServer>.Instance));
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(isLeader);
        leader.Resource.Returns(VoiceLeaderResources.AmiOwner);
        return new TrunkConnectivityTester(
            _trunks, _serverPool, leader, NullLogger<TrunkConnectivityTester>.Instance);
    }

    private void StubTrunk(long id, Trunk? trunk) =>
        _trunks.GetAsync(id, Tenant, Arg.Any<CancellationToken>())
            .Returns(trunk);

    [Fact]
    public async Task TestAsync_ShouldReportRegistered_WhenRegistrationOutputContainsRegistered()
    {
        StubTrunk(1, new Trunk { Id = 1, Name = "carrier", RegistrationUri = "sip:chicago.voip.ms" });
        StubCommand("show endpoint", " Endpoint:  t-1   Not in use   0 of inf");
        StubCommand("show registrations", " reg-t-1/sip:chicago.voip.ms   ...   Registered");
        var sut = CreateService();

        var result = await sut.TestAsync(TenantId, 1, CancellationToken.None);

        result.AuthMode.Should().Be("register");
        result.EndpointFound.Should().BeTrue();
        result.Registered.Should().BeTrue();
        result.Reachable.Should().BeTrue();
        result.IdentifyPresent.Should().BeNull();
        result.Ok.Should().BeTrue();
        result.Messages.Should().Contain(m => m.Contains("Registrado"));
    }

    [Fact]
    public async Task TestAsync_ShouldReportIdentifyPresent_WhenIpAclTrunk()
    {
        StubTrunk(2, new Trunk { Id = 2, Name = "ip-trunk", MatchHost = "54.172.60.0/30" });
        StubCommand("show endpoint", " Endpoint:  t-2   Unavailable   0 of inf");
        StubCommand("show identify", "  Identify:  ipauth-t-2/t-2\n      Match: 54.172.60.0/30");
        var sut = CreateService();

        var result = await sut.TestAsync(TenantId, 2, CancellationToken.None);

        result.AuthMode.Should().Be("ip-acl");
        result.EndpointFound.Should().BeTrue();
        result.IdentifyPresent.Should().BeTrue();
        result.Registered.Should().BeNull();
        // IP-ACL trunks have no contact until a call arrives → "Unavailable" → not reachable, but Ok
        // hinges on the identify being present, not on reachability.
        result.Reachable.Should().BeFalse();
        result.Ok.Should().BeTrue();
        result.Messages.Should().Contain(m => m.Contains("54.172.60.0/30"));
    }

    [Fact]
    public async Task TestAsync_ShouldReturnNotLeaderResult_WhenNotLeader()
    {
        // No trunk/command stubs needed — the leader gate short-circuits before any resolution.
        var sut = CreateService(isLeader: false, withServer: false);

        var result = await sut.TestAsync(TenantId, 1, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.EndpointFound.Should().BeFalse();
        result.Messages.Should().ContainSingle().Which.Should().Be("este pod no es el owner AMI");
        await _trunks.DidNotReceive().GetAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestAsync_ShouldReportEndpointNotFound_WhenOutputEmpty()
    {
        StubTrunk(3, new Trunk { Id = 3, Name = "carrier", RegistrationUri = "sip:x" });
        StubCommand("show endpoint", "Unable to find object t-3.");
        StubCommand("show registrations", "No objects found.");
        var sut = CreateService();

        var result = await sut.TestAsync(TenantId, 3, CancellationToken.None);

        result.EndpointFound.Should().BeFalse();
        result.Ok.Should().BeFalse();
        result.Messages.Should().Contain(m => m.Contains("NO presente"));
    }

    [Fact]
    public async Task TestAsync_ShouldReturnTrunkNotFound_WhenTrunkMissingForTenant()
    {
        StubTrunk(99, null);
        var sut = CreateService();

        var result = await sut.TestAsync(TenantId, 99, CancellationToken.None);

        result.EndpointFound.Should().BeFalse();
        result.Ok.Should().BeFalse();
        result.Messages.Should().ContainSingle().Which.Should().Be(ITrunkConnectivityTester.TrunkNotFoundMessage);
    }

    [Fact]
    public async Task TestAsync_ShouldReturnAmiUnavailable_WhenSendThrows()
    {
        StubTrunk(4, new Trunk { Id = 4, Name = "carrier", RegistrationUri = "sip:x" });
        _ami.SendActionAsync<CommandResponse>(Arg.Any<CommandAction>(), Arg.Any<CancellationToken>())
            .Returns<CommandResponse>(_ => throw new InvalidOperationException("ami down"));
        var sut = CreateService();

        var result = await sut.TestAsync(TenantId, 4, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Messages.Should().Contain("no se pudo consultar Asterisk (AMI)");
    }

    [Fact]
    public void TrunkConnectivityResult_ShouldRoundTripThroughApiJsonContext()
    {
        var original = new TrunkConnectivityResult(
            TrunkId: 7, EndpointId: "t-7", EndpointFound: true, AuthMode: "register",
            Registered: true, IdentifyPresent: null, Reachable: true, Ok: true,
            Messages: ["Endpoint t-7 presente", "Registrado contra el carrier"]);

        var json = JsonSerializer.Serialize(original, ApiJsonContext.Default.TrunkConnectivityResult);
        var roundTripped = JsonSerializer.Deserialize(json, ApiJsonContext.Default.TrunkConnectivityResult);

        roundTripped.Should().NotBeNull();
        roundTripped!.TrunkId.Should().Be(7);
        roundTripped.EndpointId.Should().Be("t-7");
        roundTripped.AuthMode.Should().Be("register");
        roundTripped.Registered.Should().BeTrue();
        roundTripped.IdentifyPresent.Should().BeNull();
        roundTripped.Ok.Should().BeTrue();
        roundTripped.Messages.Should().HaveCount(2).And.Contain("Registrado contra el carrier");
        // camelCase wire contract (matches the frontend hook expectations).
        json.Should().Contain("\"trunkId\":7").And.Contain("\"authMode\":\"register\"");
    }
}
