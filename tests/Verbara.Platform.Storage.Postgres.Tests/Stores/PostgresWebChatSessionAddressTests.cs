using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// The session address a WebChat conversation records is read back from the row an agent's reply loads, on a real
/// Postgres built from the real migrations (spec <c>webchat-visitor-identity</c>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresWebChatSessionAddressTests : IClassFixture<MessageCorrelationFixture>
{
    private static readonly TenantId Tenant = new("t-webchat-session-address");

    private readonly MessageCorrelationFixture _fixture;

    public PostgresWebChatSessionAddressTests(MessageCorrelationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CreateForWebChatSessionAsync_ShouldStoreTheAddress_WhenTheConversationIsReadBack()
    {
        var store = new PostgresConversationStore(await _fixture.CreateDatabaseAsync());
        var address = new ChannelAddress(ChannelType.WebChat, "webchat-7c9e6679f2a54d0b8e1a3b5c7d9f1e2a");

        var created = await new DefaultConversationLifecycleService(store, new SystemClock())
            .CreateForWebChatSessionAsync(Tenant, EntityId.New(), address, CancellationToken.None);

        var read = await store.GetByIdAsync(Tenant, created.ConversationId, CancellationToken.None);
        read.Should().NotBeNull();
        WebChatConversationMetadata.SessionAddressOf(read!).Should().Be(address);
    }
}
