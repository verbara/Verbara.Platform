using Verbara.Platform.Core;
using Verbara.Platform.Queues.Licensing;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// The in-memory twin of the licensed-agent ledger (licensed-agent-ledger, design D5/D6): the same <c>lac1</c>
/// chain, anchoring, baseline and re-anchor rules as the Postgres ledger, held in process. Appends are staged
/// and only become visible on <see cref="Staged.Commit"/>, which the writer calls after its domain write, so a
/// failed change leaves no row.
/// </summary>
public sealed class InMemoryLicenseAgentLedger
{
    private readonly ILicenseIdSource _licenses;
    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<LicenseAgentEvent>> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Sequence, string Hash, string? LicenseId)> _heads = new(StringComparer.Ordinal);

    public InMemoryLicenseAgentLedger(ILicenseIdSource licenses, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(licenses);
        ArgumentNullException.ThrowIfNull(clock);
        _licenses = licenses;
        _clock = clock;
    }

    /// <summary>Test hook: the next append fails before anything is written, as a failed ledger insert would.</summary>
    internal bool FailNextAppend { get; set; }

    /// <summary>A chain's committed rows, in sequence order (the deployment chain when <paramref name="tenantId"/> is null).</summary>
    public IReadOnlyList<LicenseAgentEvent> Events(string? tenantId)
    {
        lock (_gate)
            return _events.TryGetValue(LicenseAgentChain.ChainKeyOf(tenantId), out var rows) ? [.. rows] : [];
    }

    /// <summary>
    /// Every chain's head and committed rows, read under one lock so each chain's last row is its head
    /// (the read side of the export, licensed-agent-reporting).
    /// </summary>
    internal (IReadOnlyList<LicenseAgentChainHead> Heads, IReadOnlyList<LicenseAgentEvent> Events) Snapshot()
    {
        lock (_gate)
        {
            var heads = _heads
                .Select(kv => new LicenseAgentChainHead(LicenseAgentChain.TenantIdOf(kv.Key), kv.Value.Sequence, kv.Value.Hash, kv.Value.LicenseId))
                .ToList();
            var events = _events.Values.SelectMany(rows => rows).ToList();
            return (heads, events);
        }
    }

    /// <summary>
    /// Opens a staged append on <paramref name="tenantId"/>'s chain: anchors it (with one baseline row per
    /// agent from <paramref name="baseline"/>) when it has no head, and re-anchors it when the loaded licence
    /// changed. Nothing is visible until <see cref="Staged.Commit"/>.
    /// </summary>
    internal Staged Begin(string? tenantId, Func<IReadOnlyList<(string AgentId, string UserId, bool Counted)>> baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (FailNextAppend)
        {
            FailNextAppend = false;
            throw new InvalidOperationException("Injected licensed-agent ledger failure.");
        }

        var key = LicenseAgentChain.ChainKeyOf(tenantId);
        var licenseId = _licenses.CurrentLicenseId is { Length: > 0 } id ? id : null;
        (long Sequence, string Hash, string? LicenseId) head;
        bool exists;
        lock (_gate)
            exists = _heads.TryGetValue(key, out head);

        var staged = new Staged(this, key, tenantId, exists
            ? head
            : (0, LicenseAgentChain.Genesis(tenantId, licenseId), licenseId));
        if (!exists)
        {
            staged.Append(LicenseAgentEventKinds.ChainAnchored, null, null, null, null, null, null);
            if (tenantId is not null)
            {
                foreach (var (agentId, userId, counted) in baseline())
                    staged.Append(LicenseAgentEventKinds.AgentBaseline, agentId, userId, null, null, null, counted);
            }
        }
        else if (LicenseAgentChain.NeedsReanchor(head.LicenseId, licenseId))
        {
            staged.LicenseId = licenseId;
            staged.Append(LicenseAgentEventKinds.ChainReanchored, null, null, null, null, null, null);
        }

        return staged;
    }

    /// <summary>Rows staged on one chain; visible only once committed.</summary>
    internal sealed class Staged
    {
        private readonly InMemoryLicenseAgentLedger _ledger;
        private readonly string _key;
        private readonly string? _tenantId;
        private readonly List<LicenseAgentEvent> _rows = [];
        private long _sequence;
        private string _hash;

        public Staged(InMemoryLicenseAgentLedger ledger, string key, string? tenantId, (long Sequence, string Hash, string? LicenseId) head)
        {
            _ledger = ledger;
            _key = key;
            _tenantId = tenantId;
            (_sequence, _hash, LicenseId) = head;
        }

        public string? LicenseId { get; set; }

        public void Append(
            string kind, string? agentId, string? userId, string? actorUserId, string? conversationId, string? userStatus,
            bool? counted)
        {
            var row = LicenseAgentChain.Seal(new LicenseAgentEvent
            {
                TenantId = _tenantId,
                Sequence = _sequence + 1,
                EventId = Guid.NewGuid(),
                Kind = kind,
                OccurredAt = LicenseAgentChain.Normalize(_ledger._clock.UtcNow),
                AgentId = agentId,
                UserId = userId,
                ActorUserId = actorUserId,
                ConversationId = conversationId,
                UserStatus = userStatus,
                Counted = counted,
                LicenseId = LicenseId,
                PrevHash = _hash,
                RowHash = "",
            });
            _rows.Add(row);
            _sequence = row.Sequence;
            _hash = row.RowHash;
        }

        public void Commit()
        {
            lock (_ledger._gate)
            {
                if (!_ledger._events.TryGetValue(_key, out var rows))
                    _ledger._events[_key] = rows = [];
                rows.AddRange(_rows);
                _ledger._heads[_key] = (_sequence, _hash, LicenseId);
            }
        }
    }
}
