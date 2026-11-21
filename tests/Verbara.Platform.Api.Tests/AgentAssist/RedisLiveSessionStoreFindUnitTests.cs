using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using Verbara.Platform.Api.Services.AgentAssist;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// supervisor-whisper-reaches-the-agent (design D2, task 1.2): what <see cref="RedisAgentAssistLiveSessionStore.FindAsync"/>
/// and <see cref="RedisAgentAssistLiveSessionStore.ListAsync"/> read, against a substituted <see cref="IDatabase"/>, so the
/// unit lane (which runs no Redis) covers the read rules. The real-Redis cases are in
/// <c>Verbara.Platform.Api.Redis.Tests/AgentAssistLiveSessionStoreRedisTests</c>.
/// </summary>
public sealed class RedisLiveSessionStoreFindUnitTests
{
    private const string Tenant = "acme";
    private const string SessionId = "as-redis-unit-1";

    private static readonly DateTimeOffset Now = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly IDatabase _db = Substitute.For<IDatabase>();
    private readonly RedisAgentAssistLiveSessionStore _store;

    public RedisLiveSessionStoreFindUnitTests()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(_db);
        _store = new RedisAgentAssistLiveSessionStore(redis, _time);
    }

    private static HashEntry[] FullHash() =>
    [
        new("sessionId", SessionId),
        new("agentId", "a-9e8d7c6b5a493827"),
        new("queueName", "support"),
        new("callerIdNum", "5551234"),
        new("connectedAt", new DateTimeOffset(2026, 11, 21, 9, 59, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()),
        new("linkedId", "1712345678.90"),
        new("sentiment", "Frustrated"),
    ];

    private static readonly LiveSessionRecord Full = new(
        SessionId, Tenant, "a-9e8d7c6b5a493827", "support", "5551234",
        new DateTimeOffset(2026, 11, 21, 9, 59, 0, TimeSpan.Zero), "1712345678.90", "Frustrated");

    private void Score(double? score) =>
        _db.SortedSetScoreAsync(RedisAgentAssistLiveSessionStore.IndexKey(Tenant), SessionId, Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(score));

    private void Hash(HashEntry[] fields) =>
        _db.HashGetAllAsync(RedisAgentAssistLiveSessionStore.RecordKey(Tenant, SessionId), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(fields));

    [Fact]
    public async Task FindAsync_ShouldReturnTheMappedRecord_WhenTheScoreIsAfterNowAndTheHashExists()
    {
        Score(Now.AddSeconds(10).ToUnixTimeMilliseconds());
        Hash(FullHash());

        (await _store.FindAsync(Tenant, SessionId, CancellationToken.None)).Should().Be(Full);
    }

    [Fact]
    public async Task FindAsync_ShouldReturnARecordWithNullMembers_WhenTheHashHoldsOnlyTheSessionId()
    {
        Score(Now.AddSeconds(10).ToUnixTimeMilliseconds());
        Hash([new("sessionId", SessionId), new("connectedAt", "not-a-number")]);

        (await _store.FindAsync(Tenant, SessionId, CancellationToken.None)).Should().Be(
            new LiveSessionRecord(SessionId, Tenant, null, null, null, null, null, null));
    }

    [Theory]
    [InlineData(null)]   // no index member
    [InlineData(0L)]     // expiry == now: not strictly after
    [InlineData(-1L)]    // expired
    public async Task FindAsync_ShouldReturnNullWithoutReadingTheHash_WhenTheIndexMemberIsMissingOrNotAfterNow(long? offsetMs)
    {
        Score(offsetMs is { } offset ? Now.ToUnixTimeMilliseconds() + offset : null);
        Hash(FullHash());

        (await _store.FindAsync(Tenant, SessionId, CancellationToken.None)).Should().BeNull();
        await _db.DidNotReceive().HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task FindAsync_ShouldReturnNull_WhenTheHashIsEmpty()
    {
        Score(Now.AddSeconds(10).ToUnixTimeMilliseconds());
        Hash([]);

        (await _store.FindAsync(Tenant, SessionId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldMapEachLiveMemberAndSkipAnEmptyHash()
    {
        _db.SortedSetRangeByScoreAsync(
                RedisAgentAssistLiveSessionStore.IndexKey(Tenant), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(),
                Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue[]>([SessionId, "as-gone"]));
        Hash(FullHash());
        _db.HashGetAllAsync(RedisAgentAssistLiveSessionStore.RecordKey(Tenant, "as-gone"), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<HashEntry[]>([]));

        (await _store.ListAsync(Tenant, CancellationToken.None)).Should().Equal([Full]);
    }
}
