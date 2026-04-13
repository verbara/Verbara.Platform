using Asterisk.Platform.Core;

namespace Asterisk.Platform.Bot;

/// <summary>
/// Persistence contract for bot configuration records.
/// </summary>
public interface IBotConfigStore
{
    /// <summary>Returns the bot configuration for the given bot identifier, or <c>null</c> if not found.</summary>
    Task<BotConfiguration?> GetByIdAsync(TenantId tenantId, EntityId botId, CancellationToken ct);

    /// <summary>Returns the default (first active) bot configuration for a tenant, or <c>null</c> if none exists.</summary>
    Task<BotConfiguration?> GetDefaultAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>Returns all bot configurations for a tenant.</summary>
    Task<IReadOnlyList<BotConfiguration>> ListAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>Persists the bot configuration (insert or update).</summary>
    Task SaveAsync(BotConfiguration config, CancellationToken ct);

    /// <summary>Hard-deletes the bot configuration. Returns <c>true</c> if a record was removed.</summary>
    Task<bool> DeleteAsync(TenantId tenantId, EntityId botId, CancellationToken ct);
}
