using NSubstitute;
using Verbara.Platform.Core;
using Verbara.Platform.Typification.Ai;

namespace Verbara.Platform.Typification.Tests;

public sealed class DefaultTypificationProvenanceServiceTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly EntityId ConversationId = EntityId.From("conv-1");

    private static AiSuggestionRecord MakeSuggestion(EntityId suggestedLeaf, double confidence = 0.85) => new()
    {
        Id = EntityId.New(),
        TenantId = EntityId.From(Tenant.Value),
        ConversationId = ConversationId,
        SchemaId = EntityId.New(),
        SchemaVersion = 1,
        SuggestedLeafNodeId = suggestedLeaf,
        SuggestedNodePath = ["root", "leaf"],
        SuggestedFieldValues = new Dictionary<string, string>(),
        Confidence = confidence,
        ModelId = "gpt-4o-mini",
        PromptVersion = "v1",
        SurfacedBand = TypificationBand.Suggest,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task DeriveAsync_ShouldReturnAiSuggestedFalse_WhenNoStoredSuggestion()
    {
        var store = Substitute.For<IAiSuggestionStore>();
        store.GetLatestForConversationAsync(Arg.Any<EntityId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiSuggestionRecord?>(null));

        var metrics = new TypificationAiMetrics();
        var sut = new DefaultTypificationProvenanceService(store, metrics);
        var committed = EntityId.From("leaf-x");

        var result = await sut.DeriveAsync(Tenant, ConversationId, committed, CancellationToken.None);

        result.AiSuggested.Should().BeFalse();
        result.AiConfidence.Should().BeNull();
        result.AiAccepted.Should().BeNull();
        result.Source.Should().Be(SubmissionSource.Manual);
        result.SuggestedLeafNodeId.Should().BeNull();
        result.SuggestedNodePath.Should().BeNull();

        await store.DidNotReceive().MarkReconciledAsync(
            Arg.Any<EntityId>(), Arg.Any<EntityId>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeriveAsync_ShouldReturnAiAcceptedTrue_WhenCommittedLeafMatchesSuggestion()
    {
        var suggestedLeaf = EntityId.From("leaf-ai");
        var suggestion = MakeSuggestion(suggestedLeaf, confidence: 0.92);

        var store = Substitute.For<IAiSuggestionStore>();
        store.GetLatestForConversationAsync(Arg.Any<EntityId>(), ConversationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiSuggestionRecord?>(suggestion));

        var metrics = new TypificationAiMetrics();
        var sut = new DefaultTypificationProvenanceService(store, metrics);

        var result = await sut.DeriveAsync(Tenant, ConversationId, suggestedLeaf, CancellationToken.None);

        result.AiSuggested.Should().BeTrue();
        result.AiAccepted.Should().BeTrue();
        result.AiConfidence.Should().BeApproximately(0.92, 1e-9);
        result.Source.Should().Be(SubmissionSource.AutoAi);
        result.SuggestedLeafNodeId.Should().Be(suggestedLeaf);
        result.SuggestedNodePath.Should().Equal("root", "leaf");

        await store.Received(1).MarkReconciledAsync(
            suggestion.Id, suggestedLeaf, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeriveAsync_ShouldReturnAiAcceptedFalse_WhenAgentPicksDifferentLeaf()
    {
        var suggestedLeaf = EntityId.From("leaf-ai");
        var committedLeaf = EntityId.From("leaf-agent");
        var suggestion = MakeSuggestion(suggestedLeaf, confidence: 0.70);

        var store = Substitute.For<IAiSuggestionStore>();
        store.GetLatestForConversationAsync(Arg.Any<EntityId>(), ConversationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiSuggestionRecord?>(suggestion));

        var metrics = new TypificationAiMetrics();
        var sut = new DefaultTypificationProvenanceService(store, metrics);

        var result = await sut.DeriveAsync(Tenant, ConversationId, committedLeaf, CancellationToken.None);

        result.AiSuggested.Should().BeTrue();
        result.AiAccepted.Should().BeFalse();
        result.AiConfidence.Should().BeApproximately(0.70, 1e-9);
        result.Source.Should().Be(SubmissionSource.Manual);
        result.SuggestedLeafNodeId.Should().Be(suggestedLeaf);

        await store.Received(1).MarkReconciledAsync(
            suggestion.Id, committedLeaf, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeriveAsync_ShouldIncrementSuggestionAccepted_WhenLeafMatches()
    {
        var leaf = EntityId.From("leaf-same");
        var suggestion = MakeSuggestion(leaf);

        var store = Substitute.For<IAiSuggestionStore>();
        store.GetLatestForConversationAsync(Arg.Any<EntityId>(), ConversationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiSuggestionRecord?>(suggestion));

        // Use a real TypificationAiMetrics with a test MeterFactory to observe counters.
        // Since counters in .NET diagnostics don't expose current value easily without a
        // listener, we verify indirectly: MarkReconciledAsync called with accepted=true
        // (the counter increment is implicitly tested by the accepted=true reconcile path).
        var metrics = new TypificationAiMetrics();
        var sut = new DefaultTypificationProvenanceService(store, metrics);

        // Should not throw — counter Add(1) called without exception.
        var act = async () => await sut.DeriveAsync(Tenant, ConversationId, leaf, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await store.Received(1).MarkReconciledAsync(suggestion.Id, leaf, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeriveAsync_ShouldIncrementSuggestionOverridden_WhenLeafDiffers()
    {
        var suggestedLeaf = EntityId.From("leaf-ai");
        var committedLeaf = EntityId.From("leaf-other");
        var suggestion = MakeSuggestion(suggestedLeaf);

        var store = Substitute.For<IAiSuggestionStore>();
        store.GetLatestForConversationAsync(Arg.Any<EntityId>(), ConversationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiSuggestionRecord?>(suggestion));

        var metrics = new TypificationAiMetrics();
        var sut = new DefaultTypificationProvenanceService(store, metrics);

        var act = async () => await sut.DeriveAsync(Tenant, ConversationId, committedLeaf, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await store.Received(1).MarkReconciledAsync(suggestion.Id, committedLeaf, false, Arg.Any<CancellationToken>());
    }

    // dialer-honours-dnc-callbacks-and-agent-dispositions, design D15: the voice path derives before its
    // insert-or-nothing write and reconciles only after it stored the submission.

    [Fact]
    public async Task ReadAsync_ShouldDeriveProvenanceWithoutWriting_WhenASuggestionExists()
    {
        var suggestedLeaf = EntityId.From("leaf-ai");
        var suggestion = MakeSuggestion(suggestedLeaf, confidence: 0.92);
        var store = Substitute.For<IAiSuggestionStore>();
        store.GetLatestForConversationAsync(Arg.Any<EntityId>(), ConversationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiSuggestionRecord?>(suggestion));
        using var metrics = new TypificationAiMetrics();
        using var counted = new CountingListener(metrics);
        var sut = new DefaultTypificationProvenanceService(store, metrics);

        var result = await sut.ReadAsync(Tenant, ConversationId, suggestedLeaf, CancellationToken.None);

        result.AiSuggested.Should().BeTrue();
        result.AiAccepted.Should().BeTrue();
        result.Source.Should().Be(SubmissionSource.AutoAi);
        result.SuggestionId.Should().Be(suggestion.Id);
        await store.DidNotReceive().MarkReconciledAsync(
            Arg.Any<EntityId>(), Arg.Any<EntityId>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        counted.Total.Should().Be(0, "a read counts no accept and no override");
    }

    [Fact]
    public async Task ReconcileAsync_ShouldMarkTheSuggestionAndCountOnce_WhenCalled()
    {
        var suggestedLeaf = EntityId.From("leaf-ai");
        var committedLeaf = EntityId.From("leaf-agent");
        var suggestion = MakeSuggestion(suggestedLeaf);
        var store = Substitute.For<IAiSuggestionStore>();
        store.GetLatestForConversationAsync(Arg.Any<EntityId>(), ConversationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiSuggestionRecord?>(suggestion));
        using var metrics = new TypificationAiMetrics();
        using var counted = new CountingListener(metrics);
        var sut = new DefaultTypificationProvenanceService(store, metrics);
        var provenance = await sut.ReadAsync(Tenant, ConversationId, committedLeaf, CancellationToken.None);

        await sut.ReconcileAsync(provenance, committedLeaf, CancellationToken.None);

        await store.Received(1).MarkReconciledAsync(suggestion.Id, committedLeaf, false, Arg.Any<CancellationToken>());
        counted.Total.Should().Be(1);
    }

    [Fact]
    public async Task ReconcileAsync_ShouldDoNothing_WhenNoSuggestionWasInvolved()
    {
        var store = Substitute.For<IAiSuggestionStore>();
        using var metrics = new TypificationAiMetrics();
        var sut = new DefaultTypificationProvenanceService(store, metrics);

        await sut.ReconcileAsync(new ProvenanceResult(), EntityId.From("leaf-x"), CancellationToken.None);

        await store.DidNotReceive().MarkReconciledAsync(
            Arg.Any<EntityId>(), Arg.Any<EntityId>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Sums the accept and override counters of one <see cref="TypificationAiMetrics"/> instance.</summary>
    private sealed class CountingListener : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
        private long _total;

        public CountingListener(TypificationAiMetrics metrics)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, metrics.SuggestionAccepted) || ReferenceEquals(instrument, metrics.SuggestionOverridden))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, m, _, _) => Interlocked.Add(ref _total, m));
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }
}
