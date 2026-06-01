using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres.Stores;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Locks the 3B.2b column-projection fixes against a REAL Postgres (the InMemory store returns the
/// full entity, so it cannot catch a projection bug):
/// <list type="bullet">
///   <item><c>GetByUserIdAsync</c> must return <c>sip_password</c> + <c>extension</c> — the latent
///   bug where the self-scoped /agents/me path (its only caller) returned a null sipPassword on
///   Postgres while InMemory masked it, leaving the in-browser softphone unable to REGISTER.</item>
///   <item><c>auto_answer</c> round-trips as a tri-state: true / false / null (unset → inherit the
///   queue default), distinctly.</item>
/// </list>
/// </summary>
public class PostgresAgentStoreAutoAnswerTests : IClassFixture<AgentStoreAutoAnswerFixture>, IAsyncLifetime
{
    private readonly AgentStoreAutoAnswerFixture _fixture;
    private readonly PostgresAgentStore _sut;
    private static readonly TenantId Tenant = new("acme");

    public PostgresAgentStoreAutoAnswerTests(AgentStoreAutoAnswerFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresAgentStore(_fixture.DataSource);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static Agent NewAgent(EntityId userId, bool? autoAnswer) => new()
    {
        AgentId = EntityId.New(),
        TenantId = Tenant,
        UserId = userId,
        DisplayName = "Agent",
        State = AgentState.Offline,
        Extension = "1001",
        SipPassword = "s3cr3t",
        AutoAnswer = autoAnswer,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task GetByUserIdAsync_ShouldReturnSipPasswordAndExtension_WhenSelfScoped()
    {
        // The /agents/me regression lock: GetByUserIdAsync used to omit these columns on Postgres.
        var userId = EntityId.New();
        await _sut.SaveAsync(NewAgent(userId, autoAnswer: null), CancellationToken.None);

        var loaded = await _sut.GetByUserIdAsync(Tenant, userId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Extension.Should().Be("1001");
        loaded.SipPassword.Should().Be("s3cr3t");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task SaveAsync_ShouldRoundTripAutoAnswerTriState(bool? value)
    {
        var userId = EntityId.New();
        await _sut.SaveAsync(NewAgent(userId, autoAnswer: value), CancellationToken.None);

        var byUser = await _sut.GetByUserIdAsync(Tenant, userId, CancellationToken.None);
        var byId = await _sut.GetByIdAsync(Tenant, byUser!.AgentId, CancellationToken.None);

        byUser.AutoAnswer.Should().Be(value);
        byId!.AutoAnswer.Should().Be(value);
    }

    [Fact]
    public async Task SaveAsync_ShouldUpdateAutoAnswer_OnUpsert()
    {
        var userId = EntityId.New();
        var agent = NewAgent(userId, autoAnswer: null);
        await _sut.SaveAsync(agent, CancellationToken.None);

        agent.AutoAnswer = true;
        await _sut.SaveAsync(agent, CancellationToken.None);

        var loaded = await _sut.GetByIdAsync(Tenant, agent.AgentId, CancellationToken.None);
        loaded!.AutoAnswer.Should().BeTrue();
    }
}
