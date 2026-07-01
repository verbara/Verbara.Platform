using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Typification;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryTypificationSubmissionCorrectionStoreTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly EntityId Conversation = EntityId.From("conv-1");

    private static TypificationSubmissionCorrection MakeRecord(string leaf = "leaf-corrected") => new()
    {
        TenantId = Tenant,
        ConversationId = Conversation,
        CorrectedLeafNodeId = EntityId.From(leaf),
        CorrectedNodePath = [EntityId.From("root"), EntityId.From(leaf)],
        CorrectedByUserId = "supervisor-1",
        CorrectedAt = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenNoRecord()
    {
        var store = new InMemoryTypificationSubmissionCorrectionStore();

        var result = await store.GetAsync(Tenant, Conversation, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task InsertAsync_ShouldPersistCorrection_WhenRecorded()
    {
        var store = new InMemoryTypificationSubmissionCorrectionStore();

        await store.InsertAsync(MakeRecord(), CancellationToken.None);

        var loaded = await store.GetAsync(Tenant, Conversation, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.CorrectedByUserId.Should().Be("supervisor-1");
        loaded.CorrectedLeafNodeId.Value.Should().Be("leaf-corrected");
        loaded.CorrectedNodePath.Select(n => n.Value).Should().Equal("root", "leaf-corrected");
    }

    [Fact]
    public async Task InsertAsync_ShouldThrow_WhenConversationAlreadyCorrected()
    {
        var store = new InMemoryTypificationSubmissionCorrectionStore();
        await store.InsertAsync(MakeRecord(), CancellationToken.None);

        // Append-only, one-per-conversation: a second insert mirrors the Postgres PK violation.
        var act = async () => await store.InsertAsync(MakeRecord("leaf-other"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetAsync_ShouldScopeByConversation_WhenMultipleExist()
    {
        var store = new InMemoryTypificationSubmissionCorrectionStore();
        await store.InsertAsync(MakeRecord(), CancellationToken.None);
        await store.InsertAsync(
            new TypificationSubmissionCorrection
            {
                TenantId = Tenant,
                ConversationId = EntityId.From("conv-2"),
                CorrectedLeafNodeId = EntityId.From("leaf-2"),
                CorrectedNodePath = [EntityId.From("root"), EntityId.From("leaf-2")],
                CorrectedByUserId = "supervisor-2",
                CorrectedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        var first = await store.GetAsync(Tenant, Conversation, CancellationToken.None);
        var second = await store.GetAsync(Tenant, EntityId.From("conv-2"), CancellationToken.None);

        first!.CorrectedByUserId.Should().Be("supervisor-1");
        second!.CorrectedByUserId.Should().Be("supervisor-2");
    }
}
