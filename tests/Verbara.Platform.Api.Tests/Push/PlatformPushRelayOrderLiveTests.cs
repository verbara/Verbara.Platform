using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;
using Verbara.Sdk.Pro.Push;
using Verbara.Sdk.Pro.Push.Options;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Hosting;

namespace Verbara.Platform.Api.Tests.Push;

/// <summary>
/// Pro <c>2.28.0-pro</c>: the Redis relay delivers the events one node publishes to the other node's bus in publish
/// order. Two nodes registered as the Api registers its backplane (<c>AddVerbaraPush</c>, then
/// <c>AddVerbaraProPushRedis</c> with <see cref="PlatformPushJsonContext"/> as the payload serializer), on one
/// Testcontainers Redis; one node publishes a burst of Platform push events and the other node's bus must see each, in
/// order. This pins the bus, not SignalR clients. It is a pin, not a red-first test: the swap it guards against was a
/// thread-pool race, so its absence on an older release proves nothing; Pro's own relay-order tests are the producer's
/// proof.
/// </summary>
public sealed class PlatformPushRelayOrderLiveTests : IAsyncLifetime
{
    private const int BurstSize = 500;
    private const string Tenant = "tenant-relay-order";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private IContainer? _redis;

    [Fact]
    public async Task Relay_ShouldDeliverAPlatformBurstInPublishOrder_WhenOneNodePublishesInOrder()
    {
        using var publisher = BuildNode(ConnectionString);
        using var receiver = BuildNode(ConnectionString);
        await publisher.StartAsync();
        await receiver.StartAsync();

        var publishingBus = publisher.Services.GetRequiredService<IPushEventBus>();
        var receivingBus = receiver.Services.GetRequiredService<IPushEventBus>();

        // The receiving bus calls an observer from its single dispatch loop, so the list needs no lock.
        var received = new List<int>(BurstSize);
        var warmedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = receivingBus.AsObservable().Subscribe(evt =>
        {
            if (evt is not RemotePushEvent { EventType: "agent.state_changed" } remote)
                return;
            var payload = JsonSerializer.Deserialize(remote.RawPayload, PlatformPushJsonContext.Default.AgentStateChangedEvent);
            if (payload is null)
                return;
            if (payload.AgentId == "warm-up")
            {
                warmedUp.TrySetResult();
                return;
            }

            received.Add(int.Parse(payload.AgentId, System.Globalization.CultureInfo.InvariantCulture));
            if (received.Count == BurstSize)
                all.TrySetResult();
        });

        // The relay exposes no "subscribed" signal: publish a warm-up event until the receiving bus sees one.
        using (var guard = new CancellationTokenSource(Bound))
        {
            while (!warmedUp.Task.IsCompleted)
            {
                await publishingBus.PublishAsync(Event("warm-up"), guard.Token);
                await Task.WhenAny(warmedUp.Task, Task.Delay(TimeSpan.FromMilliseconds(200), guard.Token)); // fence-allow: LOOP-DRIVER — bounded re-publish until the receiving relay is subscribed; the loop exits on the receiver's own signal
            }
        }

        for (var i = 0; i < BurstSize; i++)
            await publishingBus.PublishAsync(Event(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), CancellationToken.None);

        await Task.WhenAny(all.Task, Task.Delay(Bound)); // fence-allow: GUARD-TIMEOUT — bounds the drain; a short count is asserted below, not hung on
        await receiver.StopAsync();
        await publisher.StopAsync();

        var firstInversion = Enumerable.Range(1, received.Count - 1).FirstOrDefault(i => received[i] < received[i - 1]);
        firstInversion.Should().Be(0,
            "the receiving bus saw the burst out of order: sequence {0} at position {1} after {2}",
            firstInversion > 0 ? received[firstInversion] : -1, firstInversion,
            firstInversion > 0 ? received[firstInversion - 1] : -1);
        received.Should().Equal(Enumerable.Range(0, BurstSize), "every event arrives once, in publish order");
    }

    private static AgentStateChangedEvent Event(string agentId) =>
        new(Tenant, agentId, "Agent " + agentId, "available", "busy");

    /// <summary>A node with the Api's push backplane registration (Api <c>Program.cs</c>, the Redis block), its bus sized to the burst.</summary>
    private static IHost BuildNode(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder();
        // The Api registers the bus with its default capacity, which evicts the oldest events beyond 256 under a burst
        // (measured: a 500-event burst arrived as 259, in order). Eviction is a loss, not a reorder, so the bus is built
        // with room for the whole burst, as Pro's own relay-order test builds it.
        builder.Services.AddVerbaraPush(o => o.BufferCapacity = 8_192);
        builder.Services.AddVerbaraProPushRedis(connectionString);
        builder.Services.Configure<PushProOptions>(o =>
        {
            o.PayloadSerializerOptions = new JsonSerializerOptions
            {
                TypeInfoResolver = PlatformPushJsonContext.Default,
            };
        });
        return builder.Build();
    }

    private string ConnectionString => $"{_redis!.Hostname}:{_redis.GetMappedPublicPort(6379)}";

    async Task IAsyncLifetime.InitializeAsync()
    {
        _redis = new ContainerBuilder("redis:7-alpine")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();
        await _redis.StartAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_redis is not null)
            await _redis.DisposeAsync();
    }
}
