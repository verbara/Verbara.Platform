using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The server pool validates a server id on every path that adds one, so the id the Api registers its Asterisk node
/// under has to be one the pool accepts. Pinned against the real pool, not a copy of its rule.
/// </summary>
public sealed class AsteriskNodeIdsTests
{
    [Fact]
    public void AsteriskNodeIds_Primary_ShouldBeAcceptedByThePool_WhenAddedAsAnExistingServer()
    {
        var pool = new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
        var server = new VerbaraServer(Substitute.For<IAmiConnection>(), NullLogger<VerbaraServer>.Instance);

        pool.AddExistingServer(AsteriskNodeIds.Primary, server);

        pool.GetServer(AsteriskNodeIds.Primary).Should().BeSameAs(server);
    }
}
