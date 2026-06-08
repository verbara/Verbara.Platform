using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Typification;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Round-trips the three typification stores against a REAL Postgres so the JSONB
/// persistence (definition wrapper, selected-node-path, field-values), the
/// version/published projections, and the scope filter are exercised end-to-end
/// (the InMemory stores cannot catch a JSONB serialization or SQL projection bug).
/// </summary>
public class PostgresTypificationStoresTests : IClassFixture<TypificationStoreFixture>, IAsyncLifetime
{
    private readonly TypificationStoreFixture _fixture;
    private readonly PostgresTypificationSchemaStore _schemas;
    private readonly PostgresSchemaBindingStore _bindings;
    private readonly PostgresTypificationSubmissionStore _submissions;
    private static readonly TenantId Tenant = new("acme");

    public PostgresTypificationStoresTests(TypificationStoreFixture fixture)
    {
        _fixture = fixture;
        _schemas = new PostgresTypificationSchemaStore(_fixture.DataSource);
        _bindings = new PostgresSchemaBindingStore(_fixture.DataSource);
        _submissions = new PostgresTypificationSubmissionStore(_fixture.DataSource);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static TypificationAiConfig EmptyAiConfig() => new()
    {
        Enabled = false,
        Mode = AiMode.SuggestOnly,
        ConfidenceThreshold = 0,
        SentimentGating = false,
        EntityFieldMap = new Dictionary<string, string>(),
    };

    private static TypificationSchema NewSchema(EntityId schemaId, int version, bool published)
    {
        var rootNode = new TypificationNode
        {
            NodeId = EntityId.New(),
            ParentNodeId = null,
            Label = "Sales",
            Code = "SALES",
            SortOrder = 0,
            IsLeaf = false,
            ChannelApplicability = [ChannelType.Voice, ChannelType.WebChat],
            Leaf = null,
        };
        var leafNode = new TypificationNode
        {
            NodeId = EntityId.New(),
            ParentNodeId = rootNode.NodeId,
            Label = "Won",
            Code = "SALES_WON",
            SortOrder = 0,
            IsLeaf = true,
            ChannelApplicability = null,
            Leaf = new LeafOutcome
            {
                Category = TypificationCategory.Success,
                TriggerRetry = false,
                RetryDelayMinutes = null,
                TriggerCallback = true,
                DialerCode = "SALE",
                IsActive = true,
            },
        };
        var field = new TypificationField
        {
            FieldId = EntityId.New(),
            Key = "amount",
            Label = "Amount",
            Type = FieldType.Number,
            Required = true,
            Options = [new FieldOption { Value = "lo", Label = "Low" }],
            Validation = new FieldValidation { Min = 0, Max = 10_000 },
            AttachToNodeId = leafNode.NodeId,
            VisibleWhen = new ConditionExpr
            {
                RefType = ConditionRef.NodeSelected,
                Ref = "SALES_WON",
                Op = ConditionOp.Eq,
                Value = "true",
            },
            PrefillSource = null,
            OptionsDataDipRef = null,
            SortOrder = 0,
        };

        return new TypificationSchema
        {
            SchemaId = schemaId,
            TenantId = Tenant,
            Name = "Sales Outcomes",
            Version = version,
            IsPublished = published,
            MaxDepth = 5,
            Nodes = [rootNode, leafNode],
            Fields = [field],
            DataDips = [],
            AiConfig = EmptyAiConfig(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = null,
        };
    }

    [Fact]
    public async Task SchemaStore_ShouldRoundTripDefinitionJsonb_WhenSavedAndLoaded()
    {
        var schemaId = EntityId.New();
        var schema = NewSchema(schemaId, version: 1, published: false);

        await _schemas.SaveAsync(schema, CancellationToken.None);
        var loaded = await _schemas.GetByIdAsync(Tenant, schemaId, version: null, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Sales Outcomes");
        loaded.Nodes.Should().HaveCount(2);
        loaded.Fields.Should().HaveCount(1);

        var leaf = loaded.Nodes.Single(n => n.IsLeaf);
        leaf.Code.Should().Be("SALES_WON");
        leaf.Leaf.Should().NotBeNull();
        leaf.Leaf!.Category.Should().Be(TypificationCategory.Success);
        leaf.Leaf.TriggerCallback.Should().BeTrue();
        leaf.Leaf.DialerCode.Should().Be("SALE");

        var root = loaded.Nodes.Single(n => !n.IsLeaf);
        root.ChannelApplicability.Should().Equal(ChannelType.Voice, ChannelType.WebChat);

        var field = loaded.Fields.Single();
        field.Key.Should().Be("amount");
        field.Required.Should().BeTrue();
        field.Validation.Should().NotBeNull();
        field.Validation!.Max.Should().Be(10_000);
        field.AttachToNodeId.Should().Be(leaf.NodeId);
        field.VisibleWhen.Should().NotBeNull();
        field.VisibleWhen!.RefType.Should().Be(ConditionRef.NodeSelected);
        field.VisibleWhen.Ref.Should().Be("SALES_WON");
        field.VisibleWhen.Op.Should().Be(ConditionOp.Eq);
    }

    [Fact]
    public async Task SchemaStore_ShouldReturnLatestPublished_WhenMultipleVersions()
    {
        var schemaId = EntityId.New();
        await _schemas.SaveAsync(NewSchema(schemaId, version: 1, published: true), CancellationToken.None);
        await _schemas.SaveAsync(NewSchema(schemaId, version: 2, published: true), CancellationToken.None);
        // A newer DRAFT (unpublished) version must not win GetLatestPublished.
        await _schemas.SaveAsync(NewSchema(schemaId, version: 3, published: false), CancellationToken.None);

        var latestPublished = await _schemas.GetLatestPublishedAsync(Tenant, schemaId, CancellationToken.None);
        var latestAny = await _schemas.GetByIdAsync(Tenant, schemaId, version: null, CancellationToken.None);

        latestPublished.Should().NotBeNull();
        latestPublished!.Version.Should().Be(2);

        latestAny.Should().NotBeNull();
        latestAny!.Version.Should().Be(3);

        // ListAsync returns the latest version per schema_id (the draft v3 here).
        var list = await _schemas.ListAsync(Tenant, CancellationToken.None);
        list.Should().ContainSingle(s => s.SchemaId == schemaId).Which.Version.Should().Be(3);
    }

    [Fact]
    public async Task BindingStore_ShouldFilterByScope_WhenListByScope()
    {
        var schemaId = EntityId.New();

        var queueBinding = new SchemaBinding
        {
            BindingId = EntityId.New(),
            TenantId = Tenant,
            Scope = BindingScope.Queue,
            ScopeRef = "queue-1",
            SchemaId = schemaId,
            SubTreeRootNodeId = null,
            Priority = 10,
        };
        var tenantBinding = new SchemaBinding
        {
            BindingId = EntityId.New(),
            TenantId = Tenant,
            Scope = BindingScope.Tenant,
            ScopeRef = null,
            SchemaId = schemaId,
            SubTreeRootNodeId = EntityId.New(),
            Priority = 0,
        };
        var otherQueueBinding = new SchemaBinding
        {
            BindingId = EntityId.New(),
            TenantId = Tenant,
            Scope = BindingScope.Queue,
            ScopeRef = "queue-2",
            SchemaId = schemaId,
            SubTreeRootNodeId = null,
            Priority = 5,
        };

        await _bindings.SaveAsync(queueBinding, CancellationToken.None);
        await _bindings.SaveAsync(tenantBinding, CancellationToken.None);
        await _bindings.SaveAsync(otherQueueBinding, CancellationToken.None);

        var queue1 = await _bindings.ListByScopeAsync(Tenant, BindingScope.Queue, "queue-1", CancellationToken.None);
        queue1.Should().ContainSingle();
        queue1[0].BindingId.Should().Be(queueBinding.BindingId);
        queue1[0].Scope.Should().Be(BindingScope.Queue);
        queue1[0].ScopeRef.Should().Be("queue-1");

        var tenantDefault = await _bindings.ListByScopeAsync(Tenant, BindingScope.Tenant, null, CancellationToken.None);
        tenantDefault.Should().ContainSingle();
        tenantDefault[0].BindingId.Should().Be(tenantBinding.BindingId);
        tenantDefault[0].ScopeRef.Should().BeNull();
        tenantDefault[0].SubTreeRootNodeId.Should().Be(tenantBinding.SubTreeRootNodeId);

        // null scopeRef must NOT match a non-null scope_ref row.
        var queueWithoutRef = await _bindings.ListByScopeAsync(Tenant, BindingScope.Queue, null, CancellationToken.None);
        queueWithoutRef.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmissionStore_ShouldRoundTripFieldValuesJsonb_WhenSaved()
    {
        var conversationId = EntityId.New();
        var path = new List<EntityId> { EntityId.New(), EntityId.New() };
        var submission = new TypificationSubmission
        {
            TenantId = Tenant,
            ConversationId = conversationId,
            AgentId = EntityId.New(),
            SchemaId = EntityId.New(),
            SchemaVersion = 2,
            SelectedNodePath = path,
            LeafNodeId = path[^1],
            FieldValues = new Dictionary<string, string>
            {
                ["amount"] = "1500",
                ["reason"] = "upsell",
            },
            Notes = "customer happy",
            AiSuggested = true,
            AiConfidence = 0.87,
            AiAccepted = true,
            Source = SubmissionSource.AutoAi,
            Duration = TimeSpan.FromSeconds(42),
            CompletedAt = DateTimeOffset.UtcNow,
        };

        await _submissions.SaveAsync(submission, CancellationToken.None);
        var loaded = await _submissions.GetByConversationIdAsync(Tenant, conversationId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.SchemaVersion.Should().Be(2);
        loaded.SelectedNodePath.Should().Equal(path);
        loaded.LeafNodeId.Should().Be(path[^1]);
        loaded.FieldValues.Should().HaveCount(2);
        loaded.FieldValues["amount"].Should().Be("1500");
        loaded.FieldValues["reason"].Should().Be("upsell");
        loaded.Notes.Should().Be("customer happy");
        loaded.AiSuggested.Should().BeTrue();
        loaded.AiConfidence.Should().BeApproximately(0.87, 1e-9);
        loaded.AiAccepted.Should().BeTrue();
        loaded.Source.Should().Be(SubmissionSource.AutoAi);
        loaded.Duration.Should().Be(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public async Task SubmissionStore_ShouldRoundTripNullableAiFields_WhenManual()
    {
        var conversationId = EntityId.New();
        var submission = new TypificationSubmission
        {
            TenantId = Tenant,
            ConversationId = conversationId,
            AgentId = EntityId.New(),
            SchemaId = EntityId.New(),
            SchemaVersion = 1,
            SelectedNodePath = [EntityId.New()],
            LeafNodeId = EntityId.New(),
            FieldValues = new Dictionary<string, string>(),
            Notes = null,
            AiSuggested = false,
            AiConfidence = null,
            AiAccepted = null,
            Source = SubmissionSource.Manual,
            Duration = TimeSpan.Zero,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        await _submissions.SaveAsync(submission, CancellationToken.None);
        var loaded = await _submissions.GetByConversationIdAsync(Tenant, conversationId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Notes.Should().BeNull();
        loaded.AiConfidence.Should().BeNull();
        loaded.AiAccepted.Should().BeNull();
        loaded.FieldValues.Should().BeEmpty();
        loaded.Source.Should().Be(SubmissionSource.Manual);
    }
}
