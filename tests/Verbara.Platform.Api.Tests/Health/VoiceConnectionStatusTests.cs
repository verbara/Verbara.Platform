using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Health;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;

namespace Verbara.Platform.Api.Tests.Health;

/// <summary>
/// readiness-without-asterisk, design D2/D5: the one seam through which the <c>asterisk</c> check and the voice
/// route gate read this pod's primary AMI connection. "Configured" comes from <c>Asterisk:Ami:Hostname</c>, the
/// same key Program.cs reads to seed the pool's <c>primary</c> server; the state comes from
/// <c>VerbaraServerPool.GetServer("primary")?.Connection.State</c>, the accessor the voice services use.
/// </summary>
public sealed class VoiceConnectionStatusTests
{
    private static IConfiguration Configuration(string? amiHostname) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Asterisk:Ami:Hostname"] = amiHostname })
            .Build();

    private static VerbaraServerPool EmptyPool() =>
        new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);

    private static VerbaraServerPool PoolWithPrimary(AmiConnectionState state)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(state);
        var pool = EmptyPool();
        pool.AddExistingServer("primary", new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance));
        return pool;
    }

    [Fact]
    public void Read_ShouldReportConfiguredAndConnected_WhenPrimaryConnectionIsConnected()
    {
        var status = new PrimaryAmiVoiceConnectionStatus(
            PoolWithPrimary(AmiConnectionState.Connected), Configuration("pbx.example.invalid"));

        var snapshot = status.Read();

        snapshot.IsConfigured.Should().BeTrue();
        snapshot.IsConnected.Should().BeTrue();
        snapshot.State.Should().Be(AmiConnectionState.Connected);
        snapshot.StateName.Should().Be("Connected");
    }

    [Fact]
    public void Read_ShouldReportNotConnectedWithItsState_WhenPrimaryConnectionIsReconnecting()
    {
        var status = new PrimaryAmiVoiceConnectionStatus(
            PoolWithPrimary(AmiConnectionState.Reconnecting), Configuration("pbx.example.invalid"));

        var snapshot = status.Read();

        snapshot.IsConfigured.Should().BeTrue();
        snapshot.IsConnected.Should().BeFalse();
        snapshot.State.Should().Be(AmiConnectionState.Reconnecting);
        snapshot.StateName.Should().Be("Reconnecting");
    }

    [Fact]
    public void Read_ShouldReportNotConnectedWithNoState_WhenConfiguredButPoolHasNoPrimaryServer()
    {
        var status = new PrimaryAmiVoiceConnectionStatus(
            EmptyPool(), Configuration("pbx.example.invalid"));

        var snapshot = status.Read();

        snapshot.IsConfigured.Should().BeTrue();
        snapshot.IsConnected.Should().BeFalse();
        snapshot.State.Should().BeNull();
        snapshot.StateName.Should().Be(VoiceConnectionSnapshot.NoPrimaryServer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_ShouldReportNotConfigured_WhenAmiHostnameIsNotSet(string? amiHostname)
    {
        var status = new PrimaryAmiVoiceConnectionStatus(EmptyPool(), Configuration(amiHostname));

        var snapshot = status.Read();

        snapshot.IsConfigured.Should().BeFalse();
        snapshot.IsConnected.Should().BeFalse();
        snapshot.StateName.Should().Be(VoiceConnectionSnapshot.NoPrimaryServer);
    }

    [Fact]
    public void Read_ShouldFollowTheLiveState_WhenTheConnectionChangesBetweenReads()
    {
        // The seam reads the pool on every call; it never caches the state it saw first.
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(AmiConnectionState.Connected);
        var pool = EmptyPool();
        pool.AddExistingServer("primary", new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance));
        var status = new PrimaryAmiVoiceConnectionStatus(pool, Configuration("pbx.example.invalid"));

        status.Read().IsConnected.Should().BeTrue();
        connection.State.Returns(AmiConnectionState.Reconnecting);

        status.Read().IsConnected.Should().BeFalse();
    }
}
