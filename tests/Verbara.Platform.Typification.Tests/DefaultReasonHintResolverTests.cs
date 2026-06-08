using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Resolution;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Typification.Tests;

public sealed class DefaultReasonHintResolverTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly EntityId QueueId = EntityId.From("queue-7");
    private const string Did = "+15551234567";

    // ---------- precedence ----------

    [Fact]
    public async Task ResolveAsync_ShouldPreferDidOverQueueAndChannel_WhenMultipleScopesMatch()
    {
        var store = new FakeReasonHintStore();
        store.Add(Hint("h-did", ReasonHintScope.Did, Did, "[\"DID\"]"));
        store.Add(Hint("h-queue", ReasonHintScope.Queue, QueueId.Value, "[\"QUEUE\"]"));
        store.Add(Hint("h-channel", ReasonHintScope.Channel, nameof(ChannelType.Voice), "[\"CHANNEL\"]"));

        var sut = new DefaultReasonHintResolver(store);

        var result = await sut.ResolveAsync(Tenant, Did, QueueId, ChannelType.Voice, CancellationToken.None);

        result.Should().NotBeNull();
        result!.HintId.Should().Be(EntityId.From("h-did"));
    }

    [Fact]
    public async Task ResolveAsync_ShouldFallBackToQueue_WhenNoDidHintButQueueHintExists()
    {
        var store = new FakeReasonHintStore();
        // DID is supplied but has NO active hint → must fall through to Queue.
        store.Add(Hint("h-queue", ReasonHintScope.Queue, QueueId.Value, "[\"QUEUE\"]"));
        store.Add(Hint("h-channel", ReasonHintScope.Channel, nameof(ChannelType.Voice), "[\"CHANNEL\"]"));

        var sut = new DefaultReasonHintResolver(store);

        var result = await sut.ResolveAsync(Tenant, Did, QueueId, ChannelType.Voice, CancellationToken.None);

        result.Should().NotBeNull();
        result!.HintId.Should().Be(EntityId.From("h-queue"));
    }

    [Fact]
    public async Task ResolveAsync_ShouldFallBackToChannel_WhenNoDidOrQueueHint()
    {
        var store = new FakeReasonHintStore();
        // DID and Queue are both supplied but neither has an active hint → fall to Channel.
        store.Add(Hint("h-channel", ReasonHintScope.Channel, nameof(ChannelType.Voice), "[\"CHANNEL\"]"));

        var sut = new DefaultReasonHintResolver(store);

        var result = await sut.ResolveAsync(Tenant, Did, QueueId, ChannelType.Voice, CancellationToken.None);

        result.Should().NotBeNull();
        result!.HintId.Should().Be(EntityId.From("h-channel"));
    }

    // ---------- tie-break ----------

    [Fact]
    public async Task ResolveAsync_ShouldRespectPriorityThenIdTiebreak_WhenSameScopeMultipleHints()
    {
        var store = new FakeReasonHintStore();
        // Highest priority wins regardless of insertion order.
        store.Add(Hint("h-low", ReasonHintScope.Did, Did, "[\"LOW\"]", priority: 1));
        store.Add(Hint("h-high", ReasonHintScope.Did, Did, "[\"HIGH\"]", priority: 9));
        // Same priority as the winner → tie-break by HintId ordinal ("h-high" < "h-zigh").
        store.Add(Hint("h-zigh", ReasonHintScope.Did, Did, "[\"TIE\"]", priority: 9));

        var sut = new DefaultReasonHintResolver(store);

        var result = await sut.ResolveAsync(Tenant, Did, queueId: null, ChannelType.Voice, CancellationToken.None);

        result.Should().NotBeNull();
        result!.HintId.Should().Be(EntityId.From("h-high"));
    }

    // ---------- active filter ----------

    [Fact]
    public async Task ResolveAsync_ShouldIgnoreInactiveHints_WhenResolving()
    {
        var store = new FakeReasonHintStore();
        // The higher-priority hint is inactive → the active lower-priority one wins.
        store.Add(Hint("h-inactive", ReasonHintScope.Did, Did, "[\"INACTIVE\"]", priority: 9, isActive: false));
        store.Add(Hint("h-active", ReasonHintScope.Did, Did, "[\"ACTIVE\"]", priority: 1));

        var sut = new DefaultReasonHintResolver(store);

        var result = await sut.ResolveAsync(Tenant, Did, queueId: null, ChannelType.Voice, CancellationToken.None);

        result.Should().NotBeNull();
        result!.HintId.Should().Be(EntityId.From("h-active"));
    }

    // ---------- no match ----------

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenNoHintMatches()
    {
        var store = new FakeReasonHintStore();
        // A hint exists, but for a different channel only → no scope matches this context.
        store.Add(Hint("h-other", ReasonHintScope.Channel, nameof(ChannelType.Email), "[\"OTHER\"]"));

        var sut = new DefaultReasonHintResolver(store);

        var result = await sut.ResolveAsync(Tenant, Did, QueueId, ChannelType.Voice, CancellationToken.None);

        result.Should().BeNull();
    }

    // ---------- helpers ----------

    private static ReasonHint Hint(
        string id,
        ReasonHintScope scope,
        string scopeRef,
        string reasonPath,
        int priority = 0,
        bool isActive = true) =>
        new()
        {
            HintId = EntityId.From(id),
            TenantId = Tenant,
            Scope = scope,
            ScopeRef = scopeRef,
            ReasonPath = reasonPath,
            Priority = priority,
            IsActive = isActive,
        };

    private sealed class FakeReasonHintStore : IReasonHintStore
    {
        private readonly List<ReasonHint> _hints = [];

        public void Add(ReasonHint hint) => _hints.Add(hint);

        public Task<IReadOnlyList<ReasonHint>> ListByScopeAsync(
            TenantId tenantId, ReasonHintScope scope, string scopeRef, CancellationToken ct)
        {
            IReadOnlyList<ReasonHint> matches = _hints
                .Where(h => h.TenantId == tenantId && h.Scope == scope && h.ScopeRef == scopeRef)
                .ToList();
            return Task.FromResult(matches);
        }

        public Task<ReasonHint?> GetByIdAsync(TenantId tenantId, EntityId hintId, CancellationToken ct) =>
            Task.FromResult(_hints.FirstOrDefault(h => h.TenantId == tenantId && h.HintId == hintId));

        public Task<IReadOnlyList<ReasonHint>> ListAsync(TenantId tenantId, CancellationToken ct)
        {
            IReadOnlyList<ReasonHint> all = _hints.Where(h => h.TenantId == tenantId).ToList();
            return Task.FromResult(all);
        }

        public Task SaveAsync(ReasonHint hint, CancellationToken ct)
        {
            _hints.Add(hint);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(TenantId tenantId, EntityId hintId, CancellationToken ct)
        {
            _hints.RemoveAll(h => h.TenantId == tenantId && h.HintId == hintId);
            return Task.CompletedTask;
        }
    }
}
