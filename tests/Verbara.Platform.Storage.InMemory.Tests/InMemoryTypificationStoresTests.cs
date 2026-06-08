using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Typification;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryTypificationStoresTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    private static TypificationSchema MakeSchema(EntityId schemaId, int version, bool published) => new()
    {
        SchemaId = schemaId,
        TenantId = Tenant,
        Name = "Schema",
        Version = version,
        IsPublished = published,
        MaxDepth = 5,
        Nodes = [],
        Fields = [],
        DataDips = [],
        AiConfig = new TypificationAiConfig
        {
            Enabled = false,
            Mode = AiMode.SuggestOnly,
            ConfidenceThreshold = 0,
            SentimentGating = false,
            EntityFieldMap = new Dictionary<string, string>(),
        },
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task GetByIdAsync_ShouldReturnHighestVersion_WhenVersionNull()
    {
        var store = new InMemoryTypificationSchemaStore();
        var schemaId = EntityId.New();
        await store.SaveAsync(MakeSchema(schemaId, 1, published: true), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaId, 3, published: false), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaId, 2, published: true), CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant, schemaId, version: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Version.Should().Be(3);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnExactVersion_WhenVersionSpecified()
    {
        var store = new InMemoryTypificationSchemaStore();
        var schemaId = EntityId.New();
        await store.SaveAsync(MakeSchema(schemaId, 1, published: true), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaId, 2, published: true), CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant, schemaId, version: 1, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Version.Should().Be(1);
    }

    [Fact]
    public async Task GetLatestPublishedAsync_ShouldIgnoreNewerDraft_WhenLatestUnpublished()
    {
        var store = new InMemoryTypificationSchemaStore();
        var schemaId = EntityId.New();
        await store.SaveAsync(MakeSchema(schemaId, 1, published: true), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaId, 2, published: true), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaId, 3, published: false), CancellationToken.None);

        var result = await store.GetLatestPublishedAsync(Tenant, schemaId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Version.Should().Be(2);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnLatestVersionPerSchema()
    {
        var store = new InMemoryTypificationSchemaStore();
        var schemaA = EntityId.New();
        var schemaB = EntityId.New();
        await store.SaveAsync(MakeSchema(schemaA, 1, published: true), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaA, 2, published: false), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaB, 1, published: true), CancellationToken.None);

        var result = await store.ListAsync(Tenant, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Single(s => s.SchemaId == schemaA).Version.Should().Be(2);
        result.Single(s => s.SchemaId == schemaB).Version.Should().Be(1);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveAllVersions()
    {
        var store = new InMemoryTypificationSchemaStore();
        var schemaId = EntityId.New();
        await store.SaveAsync(MakeSchema(schemaId, 1, published: true), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaId, 2, published: true), CancellationToken.None);

        await store.DeleteAsync(Tenant, schemaId, CancellationToken.None);

        (await store.GetByIdAsync(Tenant, schemaId, version: null, CancellationToken.None)).Should().BeNull();
        (await store.GetByIdAsync(Tenant, schemaId, version: 1, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ListByScopeAsync_ShouldFilterByScopeAndRef()
    {
        var store = new InMemorySchemaBindingStore();
        var schemaId = EntityId.New();

        var queue1 = new SchemaBinding
        {
            BindingId = EntityId.New(), TenantId = Tenant, Scope = BindingScope.Queue,
            ScopeRef = "q1", SchemaId = schemaId, Priority = 0,
        };
        var tenantDefault = new SchemaBinding
        {
            BindingId = EntityId.New(), TenantId = Tenant, Scope = BindingScope.Tenant,
            ScopeRef = null, SchemaId = schemaId, Priority = 0,
        };
        await store.SaveAsync(queue1, CancellationToken.None);
        await store.SaveAsync(tenantDefault, CancellationToken.None);

        var byQueue = await store.ListByScopeAsync(Tenant, BindingScope.Queue, "q1", CancellationToken.None);
        byQueue.Should().ContainSingle().Which.BindingId.Should().Be(queue1.BindingId);

        var byTenant = await store.ListByScopeAsync(Tenant, BindingScope.Tenant, null, CancellationToken.None);
        byTenant.Should().ContainSingle().Which.BindingId.Should().Be(tenantDefault.BindingId);

        // null ref must not match a non-null scope_ref binding.
        var queueNoRef = await store.ListByScopeAsync(Tenant, BindingScope.Queue, null, CancellationToken.None);
        queueNoRef.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmissionStore_ShouldUpsertByConversation()
    {
        var store = new InMemoryTypificationSubmissionStore();
        var conversationId = EntityId.New();

        TypificationSubmission MakeSubmission(string note) => new()
        {
            TenantId = Tenant,
            ConversationId = conversationId,
            AgentId = EntityId.New(),
            SchemaId = EntityId.New(),
            SchemaVersion = 1,
            SelectedNodePath = [EntityId.New()],
            LeafNodeId = EntityId.New(),
            FieldValues = new Dictionary<string, string>(),
            Notes = note,
            Source = SubmissionSource.Manual,
            Duration = TimeSpan.Zero,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        await store.SaveAsync(MakeSubmission("first"), CancellationToken.None);
        await store.SaveAsync(MakeSubmission("second"), CancellationToken.None);

        var loaded = await store.GetByConversationIdAsync(Tenant, conversationId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Notes.Should().Be("second");
    }
}
