using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using NSubstitute;
using Xunit;
using FluentAssertions;

namespace Verbara.Platform.Queues.Tests.Services;

public class AgentCapacityServiceTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId AgentId = EntityId.From("a-001");

    private static Agent MakeAgent() => new()
    {
        AgentId = AgentId,
        TenantId = Tenant,
        UserId = EntityId.From("u-001"),
        DisplayName = "Agent",
        State = AgentState.Available,
        CapacityOverride = new ChannelCapacityOverride(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Builds the SUT with a real (present) agent and a substituted resolver returning the supplied
    /// effective capacity. W6-A4 reads effective capacity from the resolver, not the agent entity.
    /// </summary>
    private static (InMemoryAgentCapacityService Sut, IAgentStore Store, IAgentCapacityResolver Resolver) CreateSut(
        ChannelCapacity? effective = null,
        Agent? agent = null)
    {
        var store = Substitute.For<IAgentStore>();
        store.GetByIdAsync(Tenant, AgentId, Arg.Any<CancellationToken>())
             .Returns(agent ?? MakeAgent());

        var resolver = Substitute.For<IAgentCapacityResolver>();
        resolver.ResolveAsync(Tenant, AgentId, Arg.Any<CancellationToken>())
                .Returns(effective ?? new ChannelCapacity());

        return (new InMemoryAgentCapacityService(store, resolver), store, resolver);
    }

    [Fact]
    public async Task HasCapacity_ShouldReturnTrue_WhenUnderLimit()
    {
        var (sut, _, _) = CreateSut(new ChannelCapacity { MaxVoice = 1 });

        var result = await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasCapacity_ShouldReturnFalse_WhenAtLimit()
    {
        var (sut, _, _) = CreateSut(new ChannelCapacity { MaxVoice = 1 });
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        var result = await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Reserve_ShouldIncrementLoad()
    {
        var (sut, _, _) = CreateSut();
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        var load = await sut.GetCurrentLoadAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        load.Should().Be(1);
    }

    [Fact]
    public async Task Release_ShouldDecrementLoad()
    {
        var (sut, _, _) = CreateSut();
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);
        await sut.ReleaseAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        var load = await sut.GetCurrentLoadAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        load.Should().Be(1);
    }

    [Fact]
    public async Task Release_ShouldNotGoBelowZero()
    {
        var (sut, _, _) = CreateSut();
        await sut.ReleaseAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        var load = await sut.GetCurrentLoadAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        load.Should().Be(0);
    }

    [Fact]
    public async Task GetCurrentLoad_ShouldReturnZero_WhenNoReservations()
    {
        var (sut, _, _) = CreateSut();

        var load = await sut.GetCurrentLoadAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        load.Should().Be(0);
    }

    [Fact]
    public async Task HasCapacity_ShouldReturnFalse_WhenAgentNotFound()
    {
        var store = Substitute.For<IAgentStore>();
        store.GetByIdAsync(Tenant, AgentId, Arg.Any<CancellationToken>())
             .Returns((Agent?)null);
        var resolver = Substitute.For<IAgentCapacityResolver>();
        resolver.ResolveAsync(Tenant, AgentId, Arg.Any<CancellationToken>())
                .Returns(new ChannelCapacity());
        var sut = new InMemoryAgentCapacityService(store, resolver);

        var result = await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Reserve_ShouldHandleConcurrentCalls()
    {
        var (sut, _, _) = CreateSut();
        var tasks = new List<Task>();

        for (var i = 0; i < 10; i++)
            tasks.Add(sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None));

        await Task.WhenAll(tasks);

        var load = await sut.GetCurrentLoadAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);
        load.Should().Be(10);
    }

    // ─── W6-A4 — chat-family pooling ────────────────────────────────────────────

    [Fact]
    public async Task HasCapacityAsync_ShouldPoolAllChatSubchannels_WhenMultipleChatTypesReserved()
    {
        var (sut, _, _) = CreateSut(new ChannelCapacity { MaxChat = 3, MaxTotal = 5 });

        // Three DISTINCT chat sub-channels fill the single pooled bucket (MaxChat=3).
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.WhatsApp, CancellationToken.None);
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Messenger, CancellationToken.None);
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Instagram, CancellationToken.None);

        (await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.WebChat, CancellationToken.None)).Should().BeFalse();
        (await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Telegram, CancellationToken.None)).Should().BeFalse();

        // Releasing one chat sub-channel re-opens a single pooled slot.
        await sut.ReleaseAsync(Tenant, AgentId, ChannelType.WhatsApp, CancellationToken.None);

        (await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.WebChat, CancellationToken.None)).Should().BeTrue();
    }

    // ─── W6-A4 — MaxTotal (async-only) ──────────────────────────────────────────

    [Fact]
    public async Task HasCapacityAsync_ShouldBlockAsyncChannel_WhenAsyncTotalAtMaxTotal()
    {
        var (sut, _, _) = CreateSut(new ChannelCapacity { MaxChat = 3, MaxEmail = 5, MaxSms = 3, MaxTotal = 4 });

        // asyncTotal = 3 chats + 1 email = 4 == MaxTotal.
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.WebChat, CancellationToken.None);
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.WhatsApp, CancellationToken.None);
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Messenger, CancellationToken.None);
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Email, CancellationToken.None);

        // Per-channel room remains (email 1<5, sms 0<3) but MaxTotal blocks both async channels.
        (await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Email, CancellationToken.None)).Should().BeFalse();
        (await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Sms, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task HasCapacityAsync_ShouldAllowVoice_WhenAsyncTotalAtMaxTotal()
    {
        var (sut, _, _) = CreateSut(new ChannelCapacity { MaxVoice = 1, MaxChat = 3, MaxEmail = 5, MaxSms = 3, MaxTotal = 4 });

        // Same loaded state: asyncTotal = 4 == MaxTotal, voiceLoad = 0.
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.WebChat, CancellationToken.None);
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.WhatsApp, CancellationToken.None);
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Messenger, CancellationToken.None);
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Email, CancellationToken.None);

        // Voice is the exclusive lane — NOT counted toward MaxTotal.
        (await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task HasCapacityAsync_ShouldBlockVoice_WhenVoiceLoadAtMax()
    {
        var (sut, _, _) = CreateSut(new ChannelCapacity { MaxVoice = 1 });
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        (await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task HasCapacityAsync_ShouldUseEffectiveOverride_WhenAgentOverridesChat()
    {
        // The resolver yields the EFFECTIVE MaxChat=5 (per-agent override merged over defaults).
        var (sut, _, _) = CreateSut(new ChannelCapacity { MaxChat = 5, MaxTotal = 10 });

        for (var i = 0; i < 5; i++)
        {
            (await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.WebChat, CancellationToken.None)).Should().BeTrue();
            await sut.ReserveAsync(Tenant, AgentId, ChannelType.WebChat, CancellationToken.None);
        }

        // The 6th chat is blocked by the elevated per-agent MaxChat=5.
        (await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.WebChat, CancellationToken.None)).Should().BeFalse();
    }
}
