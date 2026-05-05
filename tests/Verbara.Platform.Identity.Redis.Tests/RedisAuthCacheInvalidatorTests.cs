using System.Collections.Concurrent;
using Verbara.Platform.Identity.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Verbara.Platform.Identity.Redis.Tests;

/// <summary>
/// AHH Phase 1 — wire-format dispatch tests for <see cref="RedisAuthCacheInvalidator"/>.
/// The full pubsub round-trip (subscribe → receive → dispatch) is exercised
/// in the multi-replica integration test in Phase 3 (Testcontainers Redis
/// fixture). These unit tests only cover message-parser correctness +
/// self-suppression — both are AOT-trivial pure-string logic.
/// </summary>
public sealed class RedisAuthCacheInvalidatorTests
{
    [Fact]
    public void OnRedisMessage_ShouldDispatchTenantAuth_WhenTypeMatchesAndOriginatorIsRemote()
    {
        var sink = new RecordingSink();
        var sut = NewInvalidator(sink);

        // "remote-id|tenant-auth|tenant-abc"
        sut.OnRedisMessage($"remote-id|tenant-auth|tenant-abc");

        sink.TenantAuthInvalidations.Should().ContainSingle().Which.Should().Be("tenant-abc");
    }

    [Fact]
    public void OnRedisMessage_ShouldDispatchUser_WhenWireFormatIsComplete()
    {
        var sink = new RecordingSink();
        var sut = NewInvalidator(sink);

        sut.OnRedisMessage("remote-id|user|t1|u1|u@example.com");

        sink.UserInvalidations.Should().ContainSingle().Which
            .Should().Be(("t1", "u1", "u@example.com"));
    }

    [Fact]
    public void OnRedisMessage_ShouldDispatchUser_WhenEmailFieldIsEmpty()
    {
        var sink = new RecordingSink();
        var sut = NewInvalidator(sink);

        // Empty email: the by-id key is invalidated; remote replicas recompute the
        // by-email key on their next miss.
        sut.OnRedisMessage("remote-id|user|t1|u1|");

        sink.UserInvalidations.Should().ContainSingle().Which
            .Should().Be(("t1", "u1", (string?)null));
    }

    [Fact]
    public void OnRedisMessage_ShouldDispatchPermissions_WhenTypeMatches()
    {
        var sink = new RecordingSink();
        var sut = NewInvalidator(sink);

        sut.OnRedisMessage("remote-id|permissions|t1|u1");

        sink.PermissionsInvalidations.Should().ContainSingle().Which.Should().Be(("t1", "u1"));
    }

    [Fact]
    public void OnRedisMessage_ShouldIgnoreOwnPublishes_WhenOriginatorIdMatchesInstance()
    {
        var sink = new RecordingSink();
        var sut = NewInvalidator(sink);

        // Use the SUT's own instance id as the originator — must self-suppress.
        sut.OnRedisMessage($"{sut.InstanceId}|tenant-auth|tenant-abc");

        sink.TenantAuthInvalidations.Should().BeEmpty();
    }

    [Fact]
    public void OnRedisMessage_ShouldIgnoreUnknownTypes_WhenWireFormatIsExtendedFutureProof()
    {
        var sink = new RecordingSink();
        var sut = NewInvalidator(sink);

        sut.OnRedisMessage("remote-id|some-future-type|arg1|arg2");

        sink.TenantAuthInvalidations.Should().BeEmpty();
        sink.UserInvalidations.Should().BeEmpty();
        sink.PermissionsInvalidations.Should().BeEmpty();
    }

    [Fact]
    public void OnRedisMessage_ShouldIgnoreMalformedMessages_WhenFieldCountIsTooLow()
    {
        var sink = new RecordingSink();
        var sut = NewInvalidator(sink);

        sut.OnRedisMessage("just-one-field");
        sut.OnRedisMessage("");
        sut.OnRedisMessage("remote-id|user");
        sut.OnRedisMessage("remote-id|tenant-auth");
        sut.OnRedisMessage("remote-id|permissions");

        sink.TenantAuthInvalidations.Should().BeEmpty();
        sink.UserInvalidations.Should().BeEmpty();
        sink.PermissionsInvalidations.Should().BeEmpty();
    }

    [Fact]
    public void OnRedisMessage_ShouldDispatchToAllSinks_WhenMultipleAreRegistered()
    {
        var sink1 = new RecordingSink();
        var sink2 = new RecordingSink();
        var sut = NewInvalidator(sink1, sink2);

        sut.OnRedisMessage("remote-id|user|t1|u1|u@example.com");

        sink1.UserInvalidations.Should().ContainSingle();
        sink2.UserInvalidations.Should().ContainSingle();
    }

    private static RedisAuthCacheInvalidator NewInvalidator(params ILocalAuthCacheInvalidationSink[] sinks)
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        return new RedisAuthCacheInvalidator(
            redis,
            sinks,
            NullLogger<RedisAuthCacheInvalidator>.Instance);
    }

    private sealed class RecordingSink : ILocalAuthCacheInvalidationSink
    {
        public ConcurrentBag<string> TenantAuthInvalidations { get; } = [];
        public ConcurrentBag<(string TenantId, string UserId, string? Email)> UserInvalidations { get; } = [];
        public ConcurrentBag<(string TenantId, string UserId)> PermissionsInvalidations { get; } = [];

        public void InvalidateTenantAuth(string tenantId)
            => TenantAuthInvalidations.Add(tenantId);

        public void InvalidateUser(string tenantId, string userId, string? email)
            => UserInvalidations.Add((tenantId, userId, email));

        public void InvalidatePermissions(string tenantId, string userId)
            => PermissionsInvalidations.Add((tenantId, userId));
    }
}
