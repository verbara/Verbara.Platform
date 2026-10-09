using Verbara.Platform.Queues.Licensing;

namespace Verbara.Platform.Queues.Tests.Licensing;

/// <summary>Builds a sealed, linked chain in memory, the way the ledger appends it.</summary>
internal sealed class LicenseAgentChainBuilder
{
    private readonly List<LicenseAgentEvent> _rows = [];
    private readonly string? _tenantId;
    private string? _licenseId;

    private LicenseAgentChainBuilder(string? tenantId, string? licenseId)
    {
        _tenantId = tenantId;
        _licenseId = licenseId;
    }

    public IReadOnlyList<LicenseAgentEvent> Rows => _rows;

    public static LicenseAgentChainBuilder Anchor(string? tenantId, string? licenseId, DateTimeOffset at)
    {
        var builder = new LicenseAgentChainBuilder(tenantId, licenseId);
        builder.Add(LicenseAgentEventKinds.ChainAnchored, at, null, null, null, null, null);
        return builder;
    }

    public LicenseAgentChainBuilder Append(
        string kind, DateTimeOffset at, string agentId, string userId, bool counted, string? userStatus = null,
        string? conversationId = null)
    {
        Add(kind, at, agentId, userId, counted, userStatus, conversationId);
        return this;
    }

    public LicenseAgentChainBuilder Reanchor(string? licenseId, DateTimeOffset at)
    {
        _licenseId = licenseId;
        Add(LicenseAgentEventKinds.ChainReanchored, at, null, null, null, null, null);
        return this;
    }

    private void Add(
        string kind, DateTimeOffset at, string? agentId, string? userId, bool? counted, string? userStatus,
        string? conversationId)
    {
        var prev = _rows.Count == 0 ? LicenseAgentChain.Genesis(_tenantId, _licenseId) : _rows[^1].RowHash;
        _rows.Add(LicenseAgentChain.Seal(new LicenseAgentEvent
        {
            TenantId = _tenantId,
            Sequence = _rows.Count + 1,
            EventId = Guid.NewGuid(),
            Kind = kind,
            OccurredAt = LicenseAgentChain.Normalize(at),
            AgentId = agentId,
            UserId = userId,
            ActorUserId = agentId is null ? null : "admin",
            ConversationId = conversationId,
            UserStatus = userStatus,
            Counted = counted,
            LicenseId = _licenseId,
            PrevHash = prev,
            RowHash = "",
        }));
    }
}
