using NSubstitute;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Resolution;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Typification.Tests;

public sealed class DefaultAutonomousDispositionPolicyTests
{
    private static readonly TenantId Tenant = new("tenant-auto");
    private static readonly EntityId ConversationId = EntityId.From("conv-auto");
    private static readonly EntityId SchemaId = EntityId.From("schema-auto");
    private static readonly EntityId LeafNodeId = EntityId.From("leaf-completed");

    private const LicenseFeature FullLicense =
        LicenseFeature.AdvancedTypification | LicenseFeature.TypificationAi;

    // ── Builders ──────────────────────────────────────────────────────────────

    private static Conversation Conversation(ConversationState state = ConversationState.WrapUp) =>
        new()
        {
            ConversationId = ConversationId,
            TenantId = Tenant,
            ContactId = EntityId.From("contact-1"),
            Channel = ChannelType.WhatsApp,
            State = state,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

    private static AiSuggestionRecord Suggestion(
        double confidence = 0.97,
        EntityId? leafNodeId = null) =>
        new()
        {
            Id = EntityId.From("sugg-1"),
            TenantId = EntityId.From(Tenant.Value),
            ConversationId = ConversationId,
            SchemaId = SchemaId,
            SchemaVersion = 1,
            SuggestedLeafNodeId = leafNodeId ?? LeafNodeId,
            SuggestedNodePath = ["root", "completed"],
            SuggestedFieldValues = new Dictionary<string, string>(),
            Confidence = confidence,
            ModelId = "gpt-4o-mini",
            PromptVersion = "v1",
            SurfacedBand = TypificationBand.None,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

    private static TypificationSchema Schema(
        double autonomousThreshold = 0.95,
        bool leafIsLeaf = true,
        EntityId? leafNodeId = null) =>
        new()
        {
            SchemaId = SchemaId,
            TenantId = Tenant,
            Name = "auto-test",
            Version = 1,
            IsPublished = true,
            Nodes =
            [
                new TypificationNode
                {
                    NodeId = leafNodeId ?? LeafNodeId,
                    Label = "Completed",
                    Code = "COMPLETED",
                    IsLeaf = leafIsLeaf,
                    Leaf = leafIsLeaf
                        ? new LeafOutcome { Category = TypificationCategory.Success }
                        : null,
                },
            ],
            Fields = [],
            DataDips = [],
            AiConfig = new TypificationAiConfig
            {
                Enabled = true,
                AutonomousThreshold = autonomousThreshold,
                EntityFieldMap = new Dictionary<string, string>(),
            },
        };

    private static TenantAutonomousDisposition ActiveGate() =>
        new()
        {
            TenantId = Tenant,
            AttestedByUserId = "admin-1",
            AttestedAt = DateTimeOffset.UnixEpoch,
        };

    private static TenantAutonomousDisposition RevokedGate() =>
        new()
        {
            TenantId = Tenant,
            AttestedByUserId = "admin-1",
            AttestedAt = DateTimeOffset.UnixEpoch,
            RevokedAt = DateTimeOffset.UnixEpoch.AddDays(1),
            RevokedByUserId = "admin-2",
        };

    // ── SUT factory ───────────────────────────────────────────────────────────

    private static DefaultAutonomousDispositionPolicy BuildSut(
        TenantAutonomousDisposition? gate,
        LicenseFeature licensedFeatures,
        TypificationSchema? resolvedSchema)
    {
        var gateStore = Substitute.For<ITenantAutonomousDispositionStore>();
        gateStore.GetAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(gate));

        var licenseStatus = Substitute.For<ILicenseStatus>();
        licenseStatus.LicensedFeatures.Returns(licensedFeatures);

        var resolver = Substitute.For<ITypificationResolver>();
        resolver.ResolveForConversationAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                resolvedSchema is null
                    ? null
                    : new ResolvedTypification(resolvedSchema, SubtreeRoot: null, resolvedSchema.AiConfig)));

        return new DefaultAutonomousDispositionPolicy(gateStore, licenseStatus, resolver);
    }

    // ── Gating-path tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_ShouldSkipGateDisabled_WhenNoActivationRecord()
    {
        var sut = BuildSut(gate: null, FullLicense, Schema());

        var decision = await sut.EvaluateAsync(Conversation(), Suggestion(), CancellationToken.None);

        decision.ShouldCommit.Should().BeFalse();
        decision.Reason.Should().Be(AutonomousSkipReason.GateDisabled);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldSkipGateDisabled_WhenActivationRevoked()
    {
        var sut = BuildSut(RevokedGate(), FullLicense, Schema());

        var decision = await sut.EvaluateAsync(Conversation(), Suggestion(), CancellationToken.None);

        decision.ShouldCommit.Should().BeFalse();
        decision.Reason.Should().Be(AutonomousSkipReason.GateDisabled);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldSkipLicenseMissing_WhenAiFeatureNotLicensed()
    {
        // Holds AdvancedTypification but NOT TypificationAi → both-flags check fails.
        var sut = BuildSut(ActiveGate(), LicenseFeature.AdvancedTypification, Schema());

        var decision = await sut.EvaluateAsync(Conversation(), Suggestion(), CancellationToken.None);

        decision.ShouldCommit.Should().BeFalse();
        decision.Reason.Should().Be(AutonomousSkipReason.LicenseMissing);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldSkipNoSuggestion_WhenSuggestionNull()
    {
        var sut = BuildSut(ActiveGate(), FullLicense, Schema());

        var decision = await sut.EvaluateAsync(Conversation(), suggestion: null, CancellationToken.None);

        decision.ShouldCommit.Should().BeFalse();
        decision.Reason.Should().Be(AutonomousSkipReason.NoSuggestion);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldSkipBelowThreshold_WhenConfidenceUnderAutonomousThreshold()
    {
        // Suggestion confidence 0.94 < schema AutonomousThreshold 0.95.
        var sut = BuildSut(ActiveGate(), FullLicense, Schema(autonomousThreshold: 0.95));

        var decision = await sut.EvaluateAsync(
            Conversation(), Suggestion(confidence: 0.94), CancellationToken.None);

        decision.ShouldCommit.Should().BeFalse();
        decision.Reason.Should().Be(AutonomousSkipReason.BelowThreshold);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldSkipNotWrapUp_WhenConversationAlreadyClosed()
    {
        var sut = BuildSut(ActiveGate(), FullLicense, Schema());

        var decision = await sut.EvaluateAsync(
            Conversation(ConversationState.Closed), Suggestion(), CancellationToken.None);

        decision.ShouldCommit.Should().BeFalse();
        decision.Reason.Should().Be(AutonomousSkipReason.NotWrapUp);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldSkipSchemaMutation_WhenSuggestedLeafNoLongerLeaf()
    {
        // The node still exists in the schema but is no longer a leaf (taxonomy mutated).
        var sut = BuildSut(ActiveGate(), FullLicense, Schema(leafIsLeaf: false));

        var decision = await sut.EvaluateAsync(Conversation(), Suggestion(), CancellationToken.None);

        decision.ShouldCommit.Should().BeFalse();
        decision.Reason.Should().Be(AutonomousSkipReason.SchemaMutation);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldSkipSchemaMutation_WhenNoSchemaResolves()
    {
        var sut = BuildSut(ActiveGate(), FullLicense, resolvedSchema: null);

        var decision = await sut.EvaluateAsync(Conversation(), Suggestion(), CancellationToken.None);

        decision.ShouldCommit.Should().BeFalse();
        decision.Reason.Should().Be(AutonomousSkipReason.SchemaMutation);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldSkipSchemaMutation_WhenSuggestedLeafAbsentFromSchema()
    {
        // Schema's only node is "leaf-completed"; the suggestion targets a now-removed node id.
        var sut = BuildSut(ActiveGate(), FullLicense, Schema());

        var decision = await sut.EvaluateAsync(
            Conversation(),
            Suggestion(leafNodeId: EntityId.From("leaf-removed")),
            CancellationToken.None);

        decision.ShouldCommit.Should().BeFalse();
        decision.Reason.Should().Be(AutonomousSkipReason.SchemaMutation);
    }

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_ShouldCommitWithLeafAndConfidence_WhenAllGatesPass()
    {
        var sut = BuildSut(ActiveGate(), FullLicense, Schema(autonomousThreshold: 0.95));

        var decision = await sut.EvaluateAsync(
            Conversation(), Suggestion(confidence: 0.97), CancellationToken.None);

        decision.ShouldCommit.Should().BeTrue();
        decision.Reason.Should().BeNull();
        decision.LeafNodeId.Should().Be(LeafNodeId);
        decision.NodePath.Should().Equal("root", "completed");
        decision.Confidence.Should().BeApproximately(0.97, 1e-9);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldCommit_WhenConfidenceExactlyAtThreshold()
    {
        // Confidence 0.95 == AutonomousThreshold 0.95 → inclusive boundary commits.
        var sut = BuildSut(ActiveGate(), FullLicense, Schema(autonomousThreshold: 0.95));

        var decision = await sut.EvaluateAsync(
            Conversation(), Suggestion(confidence: 0.95), CancellationToken.None);

        decision.ShouldCommit.Should().BeTrue();
        decision.Confidence.Should().BeApproximately(0.95, 1e-9);
    }
}
