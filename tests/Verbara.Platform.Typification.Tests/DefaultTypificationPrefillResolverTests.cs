using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Resolution;

namespace Verbara.Platform.Typification.Tests;

public sealed class DefaultTypificationPrefillResolverTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    // 3-level cascade: CITAS → REPROG → GINE (each level the parent of the next).
    private static readonly EntityId CitasId = EntityId.New();
    private static readonly EntityId ReprogId = EntityId.New();
    private static readonly EntityId GineId = EntityId.New();

    private readonly DefaultTypificationPrefillResolver _sut = new();

    // ---------- reason path (node preselect) ----------

    [Fact]
    public void ResolvePrefill_ShouldReturnFullNodePath_WhenReasonPathCodesFormValidChain()
    {
        var schema = Schema(CascadeNodes());
        var conversation = ConversationWith((TypificationMetadataKeys.ReasonPath, CodesJson("CITAS", "REPROG", "GINE")));

        var result = _sut.ResolvePrefill(schema, subtreeRoot: null, conversation);

        result.PrefilledNodePath.Should().Equal(CitasId, ReprogId, GineId);
    }

    [Fact]
    public void ResolvePrefill_ShouldReturnLongestValidPrefix_WhenChainBreaksMidway()
    {
        var schema = Schema(CascadeNodes());
        // 3rd code is unknown to the schema → keep the first two validated node ids.
        var conversation = ConversationWith((TypificationMetadataKeys.ReasonPath, CodesJson("CITAS", "REPROG", "UNKNOWN")));

        var result = _sut.ResolvePrefill(schema, subtreeRoot: null, conversation);

        result.PrefilledNodePath.Should().Equal(CitasId, ReprogId);
    }

    [Fact]
    public void ResolvePrefill_ShouldReturnEmptyPath_WhenReasonPathMissing()
    {
        var schema = Schema(CascadeNodes());
        var conversation = ConversationWith(); // no metadata at all

        var result = _sut.ResolvePrefill(schema, subtreeRoot: null, conversation);

        result.PrefilledNodePath.Should().BeEmpty();
    }

    [Fact]
    public void ResolvePrefill_ShouldNotThrow_WhenReasonPathJsonMalformed()
    {
        var schema = Schema(CascadeNodes());
        var conversation = ConversationWith((TypificationMetadataKeys.ReasonPath, "not json["));

        var act = () => _sut.ResolvePrefill(schema, subtreeRoot: null, conversation);

        act.Should().NotThrow();
        act().PrefilledNodePath.Should().BeEmpty();
    }

    [Fact]
    public void ResolvePrefill_ShouldRespectSubtreeRoot_WhenPathExitsSubtree()
    {
        var schema = Schema(CascadeNodes());
        // Captured path is CITAS→REPROG→GINE, but the binding constrains to a node
        // OUTSIDE that path → no preselect (the captured reason is in another branch).
        var foreignRoot = EntityId.New();
        var conversation = ConversationWith((TypificationMetadataKeys.ReasonPath, CodesJson("CITAS", "REPROG", "GINE")));

        var result = _sut.ResolvePrefill(schema, subtreeRoot: foreignRoot, conversation);

        result.PrefilledNodePath.Should().BeEmpty();
    }

    [Fact]
    public void ResolvePrefill_ShouldReturnPathThroughSubtreeRoot_WhenPathContainsSubtreeRoot()
    {
        var schema = Schema(CascadeNodes());
        var conversation = ConversationWith((TypificationMetadataKeys.ReasonPath, CodesJson("CITAS", "REPROG", "GINE")));

        // Sub-tree root IS in the captured path → return the full validated path as-is.
        var result = _sut.ResolvePrefill(schema, subtreeRoot: ReprogId, conversation);

        result.PrefilledNodePath.Should().Equal(CitasId, ReprogId, GineId);
    }

    // ---------- field prefill (values) ----------

    [Fact]
    public void ResolvePrefill_ShouldPrefillFieldValues_WhenFieldHasMetadataPrefillSourceAndKeyPresent()
    {
        var fields = new[]
        {
            MetadataPrefillField(key: "documentId", metadataRef: "doc_id"),
        };
        var schema = Schema(CascadeNodes(), fields);
        var conversation = ConversationWith(("doc_id", "ABC-123"));

        var result = _sut.ResolvePrefill(schema, subtreeRoot: null, conversation);

        result.PrefilledFieldValues.Should().ContainKey("documentId")
            .WhoseValue.Should().Be("ABC-123");
    }

    [Fact]
    public void ResolvePrefill_ShouldOmitField_WhenMetadataKeyAbsent()
    {
        var fields = new[]
        {
            MetadataPrefillField(key: "documentId", metadataRef: "doc_id"),
        };
        var schema = Schema(CascadeNodes(), fields);
        var conversation = ConversationWith(); // metadata key "doc_id" not present

        var result = _sut.ResolvePrefill(schema, subtreeRoot: null, conversation);

        result.PrefilledFieldValues.Should().NotContainKey("documentId");
        result.PrefilledFieldValues.Should().BeEmpty();
    }

    [Fact]
    public void ResolvePrefill_ShouldIgnoreNonMetadataPrefillKinds_WhenFieldSourceIsAiEntity()
    {
        var fields = new[]
        {
            new TypificationField
            {
                FieldId = EntityId.New(),
                Key = "aiField",
                Label = "AI Field",
                Type = FieldType.Text,
                PrefillSource = new PrefillRef { Kind = PrefillSourceKind.AiEntity, Ref = "ai_ref" },
            },
        };
        var schema = Schema(CascadeNodes(), fields);
        // Even with a matching metadata key present, AiEntity kind is ignored (P2).
        var conversation = ConversationWith(("ai_ref", "should-be-ignored"));

        var result = _sut.ResolvePrefill(schema, subtreeRoot: null, conversation);

        result.PrefilledFieldValues.Should().BeEmpty();
    }

    // ---------- helpers ----------

    // Hand-built JSON array (reflection-free) matching the reasonPath contract
    // (a JSON array of node Codes), e.g. ["CITAS","REPROG","GINE"].
    private static string CodesJson(params string[] codes) =>
        "[" + string.Join(",", codes.Select(static c => "\"" + c + "\"")) + "]";

    private static IReadOnlyList<TypificationNode> CascadeNodes() =>
    [
        new TypificationNode
        {
            NodeId = CitasId,
            ParentNodeId = null,
            Label = "Citas",
            Code = "CITAS",
            IsLeaf = false,
        },
        new TypificationNode
        {
            NodeId = ReprogId,
            ParentNodeId = CitasId,
            Label = "Reprogramación",
            Code = "REPROG",
            IsLeaf = false,
        },
        new TypificationNode
        {
            NodeId = GineId,
            ParentNodeId = ReprogId,
            Label = "Ginecología",
            Code = "GINE",
            IsLeaf = true,
            Leaf = new LeafOutcome { Category = TypificationCategory.Success },
        },
    ];

    private static TypificationField MetadataPrefillField(string key, string metadataRef) =>
        new()
        {
            FieldId = EntityId.New(),
            Key = key,
            Label = key,
            Type = FieldType.Text,
            PrefillSource = new PrefillRef { Kind = PrefillSourceKind.Metadata, Ref = metadataRef },
        };

    private static TypificationSchema Schema(
        IReadOnlyList<TypificationNode> nodes,
        IReadOnlyList<TypificationField>? fields = null) =>
        new()
        {
            SchemaId = EntityId.New(),
            TenantId = Tenant,
            Name = "schema",
            Version = 1,
            IsPublished = true,
            Nodes = nodes,
            Fields = fields ?? [],
            DataDips = [],
            AiConfig = new TypificationAiConfig { EntityFieldMap = new Dictionary<string, string>() },
        };

    private static Conversation ConversationWith(params (string Key, string Value)[] metadata)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.Voice,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        foreach (var (key, value) in metadata)
            conversation.SetMetadata(key, value);

        return conversation;
    }
}
