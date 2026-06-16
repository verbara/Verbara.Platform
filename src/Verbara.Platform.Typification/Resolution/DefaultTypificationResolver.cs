using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Typification.Resolution;

/// <summary>
/// Default <see cref="ITypificationResolver"/>: resolves a conversation against its
/// published schema bindings most-specific-wins, then by descending
/// <see cref="SchemaBinding.Priority"/> (ties broken by the most-recent
/// <see cref="SchemaBinding.CreatedAt"/>, then <see cref="SchemaBinding.BindingId"/>
/// for determinism), returning the first candidate that has a published schema.
/// </summary>
public sealed class DefaultTypificationResolver : ITypificationResolver
{
    private readonly ISchemaBindingStore _bindingStore;
    private readonly ITypificationSchemaStore _schemaStore;

    public DefaultTypificationResolver(ISchemaBindingStore bindingStore, ITypificationSchemaStore schemaStore)
    {
        _bindingStore = bindingStore;
        _schemaStore = schemaStore;
    }

    public async Task<ResolvedTypification?> ResolveForConversationAsync(Conversation conversation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var tenantId = conversation.TenantId;

        // Compare the enum NAME (e.g. "Voice") against the binding ScopeRef.
        var channel = conversation.Channel.ToString();

        var queueId = conversation.Owner is { Kind: ConversationOwnerKind.Queue } owner
            ? owner.OwnerId?.Value
            : null;

        var campaignId = conversation.Metadata.TryGetValue("campaignId", out var c) ? c : null;

        // Direction is inferred: Conversation has no Direction property (P0). Composite
        // Queue+Channel scope deferred (single-scope model).
        var direction = campaignId is not null || conversation.Metadata.ContainsKey("callAttemptId")
            ? "outbound"
            : "inbound";

        // Precedence (most-specific first): Queue → Campaign → Channel → Direction → Tenant.
        // Only scopes whose context value is non-null are queried.
        if (queueId is not null)
        {
            var resolved = await ResolveScopeAsync(tenantId, BindingScope.Queue, queueId, ct).ConfigureAwait(false);
            if (resolved is not null)
                return resolved;
        }

        if (campaignId is not null)
        {
            var resolved = await ResolveScopeAsync(tenantId, BindingScope.Campaign, campaignId, ct).ConfigureAwait(false);
            if (resolved is not null)
                return resolved;
        }

        var byChannel = await ResolveScopeAsync(tenantId, BindingScope.Channel, channel, ct).ConfigureAwait(false);
        if (byChannel is not null)
            return byChannel;

        var byDirection = await ResolveScopeAsync(tenantId, BindingScope.Direction, direction, ct).ConfigureAwait(false);
        if (byDirection is not null)
            return byDirection;

        // Tenant default: ScopeRef is ignored (null) for the tenant-wide binding.
        return await ResolveScopeAsync(tenantId, BindingScope.Tenant, scopeRef: null, ct).ConfigureAwait(false);
    }

    private async Task<ResolvedTypification?> ResolveScopeAsync(
        TenantId tenantId,
        BindingScope scope,
        string? scopeRef,
        CancellationToken ct)
    {
        var bindings = await _bindingStore.ListByScopeAsync(tenantId, scope, scopeRef, ct).ConfigureAwait(false);
        if (bindings.Count == 0)
            return null;

        // Highest Priority first; then most-recently-created wins; BindingId ordinal
        // is the ultra-final stable tie-break for determinism.
        var ordered = bindings
            .OrderByDescending(static b => b.Priority)
            .ThenByDescending(static b => b.CreatedAt)
            .ThenBy(static b => b.BindingId.Value, StringComparer.Ordinal);

        foreach (var binding in ordered)
        {
            var schema = await _schemaStore.GetLatestPublishedAsync(tenantId, binding.SchemaId, ct).ConfigureAwait(false);
            if (schema is not null)
                // E1 — effective AI config: the binding's override wins, else the schema's own.
                return new ResolvedTypification(
                    schema,
                    binding.SubTreeRootNodeId,
                    binding.AiConfigOverride ?? schema.AiConfig);
        }

        // No candidate in this scope has a published schema → fall through to the next scope.
        return null;
    }
}
