using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;
using Verbara.Platform.Typification.Ai;

namespace Verbara.Platform.Typification.Tests;

public sealed class DefaultTypificationAiClassifierTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    // 3-level cascade with two leaves under a shared root:
    //   CITAS → REPROG → { GINE (leaf), PEDIA (leaf) }
    //   SOPORTE (root, leaf) — a separate branch used for sub-tree tests.
    private static readonly EntityId CitasId = EntityId.New();
    private static readonly EntityId ReprogId = EntityId.New();
    private static readonly EntityId GineId = EntityId.New();
    private static readonly EntityId PediaId = EntityId.New();
    private static readonly EntityId SoporteId = EntityId.New();

    [Fact]
    public async Task ClassifyAsync_ShouldReturnValidatedPath_WhenLlmReturnsValidLeafCode()
    {
        var sut = ClassifierReturning("""{"leafCode":"GINE","confidence":0.9,"sentiment":"neutral"}""");
        var schema = Schema();

        var result = await sut.ClassifyAsync(schema, subtreeRoot: null, Conversation(), Transcript(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.NodePath.Should().Equal(CitasId, ReprogId, GineId);
    }

    [Fact]
    public async Task ClassifyAsync_ShouldReturnNull_WhenJsonMalformed()
    {
        var sut = ClassifierReturning("this is not json at all { broken");
        var schema = Schema();

        var result = await sut.ClassifyAsync(schema, subtreeRoot: null, Conversation(), Transcript(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ClassifyAsync_ShouldReturnNull_WhenLeafCodeUnknown()
    {
        var sut = ClassifierReturning("""{"leafCode":"DOES_NOT_EXIST","confidence":0.8}""");
        var schema = Schema();

        var result = await sut.ClassifyAsync(schema, subtreeRoot: null, Conversation(), Transcript(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ClassifyAsync_ShouldReturnNull_WhenCodeIsNotALeaf()
    {
        // REPROG is an intermediate node, not a leaf → must be rejected.
        var sut = ClassifierReturning("""{"leafCode":"REPROG","confidence":0.95}""");
        var schema = Schema();

        var result = await sut.ClassifyAsync(schema, subtreeRoot: null, Conversation(), Transcript(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ClassifyAsync_ShouldStripMarkdownFences_WhenLlmWrapsJson()
    {
        var sut = ClassifierReturning("""
            ```json
            {"leafCode":"PEDIA","confidence":0.7,"sentiment":"positive"}
            ```
            """);
        var schema = Schema();

        var result = await sut.ClassifyAsync(schema, subtreeRoot: null, Conversation(), Transcript(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.NodePath.Should().Equal(CitasId, ReprogId, PediaId);
    }

    [Fact]
    public async Task ClassifyAsync_ShouldSurfaceConfidenceAndSentiment()
    {
        var sut = ClassifierReturning("""{"leafCode":"GINE","confidence":0.42,"sentiment":"very_negative"}""");
        var schema = Schema();

        var result = await sut.ClassifyAsync(schema, subtreeRoot: null, Conversation(), Transcript(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Confidence.Should().Be(0.42);
        result.Sentiment.Should().Be("very_negative");
    }

    [Fact]
    public async Task ClassifyAsync_ShouldKeepOnlyKnownFieldKeys_WhenLlmReturnsExtraFields()
    {
        var sut = ClassifierReturning("""
            {"leafCode":"GINE","confidence":0.8,
             "fields":{"documentId":"ABC-123","unknownKey":"should-drop"}}
            """);
        var schema = Schema(WithDocumentIdField());

        var result = await sut.ClassifyAsync(schema, subtreeRoot: null, Conversation(), Transcript(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.FieldValues.Should().ContainKey("documentId").WhoseValue.Should().Be("ABC-123");
        result.FieldValues.Should().NotContainKey("unknownKey");
        result.FieldValues.Should().HaveCount(1);
    }

    [Fact]
    public async Task ClassifyAsync_ShouldReturnNull_WhenProviderThrows()
    {
        var sut = new DefaultTypificationAiClassifier(new ThrowingLlmProvider());
        var schema = Schema();

        var act = async () => await sut.ClassifyAsync(schema, subtreeRoot: null, Conversation(), Transcript(), CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeNull();
    }

    [Fact]
    public async Task ClassifyAsync_ShouldRespectSubtreeRoot_WhenLeafOutsideSubtree()
    {
        // Model picks GINE (under CITAS), but the binding constrains to the SOPORTE
        // branch → the resolved path does not pass through SOPORTE → null.
        var sut = ClassifierReturning("""{"leafCode":"GINE","confidence":0.9}""");
        var schema = Schema();

        var result = await sut.ClassifyAsync(schema, subtreeRoot: SoporteId, Conversation(), Transcript(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ClassifyAsync_ShouldReturnNull_WhenTranscriptHasNoText()
    {
        var sut = ClassifierReturning("""{"leafCode":"GINE","confidence":0.9}""");
        var schema = Schema();

        // A single non-text (image) block → no transcript text to classify.
        var noTextTranscript = new[] { Message(MessageDirection.Inbound, new ImageBlock("https://x/y.png", null, "image/png")) };

        var result = await sut.ClassifyAsync(schema, subtreeRoot: null, Conversation(), noTextTranscript, CancellationToken.None);

        result.Should().BeNull();
    }

    // ---------- helpers ----------

    private static DefaultTypificationAiClassifier ClassifierReturning(string content) =>
        new(new FakeLlmProvider(content));

    private static IReadOnlyList<Message> Transcript() =>
    [
        Message(MessageDirection.Inbound, new TextBlock("Hello, I need to reschedule my appointment.")),
        Message(MessageDirection.Outbound, new TextBlock("Sure, which specialty?")),
        Message(MessageDirection.Inbound, new TextBlock("Gynecology.")),
    ];

    private static Message Message(MessageDirection direction, MessageBlock block) =>
        new()
        {
            MessageId = EntityId.New(),
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            Direction = direction,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([block]),
            DeliveryStatus = MessageDeliveryStatus.Sent,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static Conversation Conversation() =>
        new()
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static IReadOnlyList<TypificationNode> CascadeNodes() =>
    [
        new TypificationNode { NodeId = CitasId, ParentNodeId = null, Label = "Citas", Code = "CITAS", IsLeaf = false },
        new TypificationNode { NodeId = ReprogId, ParentNodeId = CitasId, Label = "Reprogramación", Code = "REPROG", IsLeaf = false },
        new TypificationNode
        {
            NodeId = GineId, ParentNodeId = ReprogId, Label = "Ginecología", Code = "GINE", IsLeaf = true,
            Leaf = new LeafOutcome { Category = TypificationCategory.Success },
        },
        new TypificationNode
        {
            NodeId = PediaId, ParentNodeId = ReprogId, Label = "Pediatría", Code = "PEDIA", IsLeaf = true,
            Leaf = new LeafOutcome { Category = TypificationCategory.Success },
        },
        new TypificationNode
        {
            NodeId = SoporteId, ParentNodeId = null, Label = "Soporte", Code = "SOPORTE", IsLeaf = true,
            Leaf = new LeafOutcome { Category = TypificationCategory.Success },
        },
    ];

    private static TypificationField[] WithDocumentIdField() =>
    [
        new TypificationField
        {
            FieldId = EntityId.New(),
            Key = "documentId",
            Label = "Document ID",
            Type = FieldType.Text,
        },
    ];

    private static TypificationSchema Schema(IReadOnlyList<TypificationField>? fields = null) =>
        new()
        {
            SchemaId = EntityId.New(),
            TenantId = Tenant,
            Name = "schema",
            Version = 1,
            IsPublished = true,
            Nodes = CascadeNodes(),
            Fields = fields ?? [],
            DataDips = [],
            AiConfig = new TypificationAiConfig { EntityFieldMap = new Dictionary<string, string>() },
        };

    /// <summary>A fake provider that always returns canned content (the classifier prompt is irrelevant).</summary>
    private sealed class FakeLlmProvider(string content) : ILlmProvider
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            Task.FromResult(new LlmResponse(content));
    }

    /// <summary>A fake provider that simulates a transport/timeout failure.</summary>
    private sealed class ThrowingLlmProvider : ILlmProvider
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            throw new HttpRequestException("simulated provider failure");
    }
}
