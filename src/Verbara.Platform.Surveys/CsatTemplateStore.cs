using Verbara.Platform.Core;

namespace Verbara.Platform.Surveys;

/// <summary>
/// A stored per-tenant CSAT prompt template row (csat-runner Phase E), persisted in
/// <c>csat_templates</c> keyed <c>(tenant_id, template_id)</c>. Distinct from Pro's
/// resolved <c>Verbara.Sdk.Pro.CsatRunner.CsatTemplate</c> (which the
/// <c>CsatTemplateProvider</c> maps this onto after running the fallback chain):
/// this type is the storage entity, that one is the opaque contract return.
/// </summary>
/// <remarks>
/// Webchat uses i18n strings and has no per-tenant template, so <see cref="Channel"/>
/// is constrained to <c>voice</c>, <c>email</c>, or <c>sms</c> (enforced by the
/// <c>chk_csat_templates_channel</c> CHECK constraint, migration 016). <see cref="Subject"/>
/// is populated only for channels that carry one (email); it is null for voice/SMS.
/// </remarks>
public sealed class CsatTemplateEntry : ITenantScoped
{
    /// <summary>The template id, unique per <c>(tenant, template)</c>.</summary>
    public required EntityId TemplateId { get; init; }

    /// <summary>The owning tenant.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The channel the prompt is for: <c>voice</c>, <c>email</c>, or <c>sms</c>.</summary>
    public required string Channel { get; init; }

    /// <summary>The template locale (BCP-47, e.g. <c>en-US</c>).</summary>
    public required string Locale { get; init; }

    /// <summary>Subject line for channels that carry one (email); null otherwise.</summary>
    public string? Subject { get; init; }

    /// <summary>The prompt body — the email message, SMS text, or voice TTS prompt.</summary>
    public required string Body { get; init; }

    /// <summary>
    /// Whether this is a tenant default for its <c>(channel, locale)</c>. The provider's
    /// fallback chain resolves the default row when no explicit template id is requested.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>When the row was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the row was last updated (UTC); null if never updated.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Persistence abstraction for the per-tenant CSAT prompt template store (csat-runner
/// Phase E). Backs both the admin CRUD surface (<c>/api/v1/admin/csat/templates/*</c>)
/// and the in-process <c>ICsatTemplateProvider</c> fallback resolution. All data access
/// uses <c>Verbara.Sdk.Data.Npgsql</c> under Postgres (Dapper is banned — Platform/ADR-0022).
/// </summary>
public interface ICsatTemplateStore
{
    /// <summary>Returns all templates for a tenant, ordered for deterministic listing.</summary>
    Task<IReadOnlyList<CsatTemplateEntry>> GetAllAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>Returns a single template by id, or null when absent.</summary>
    Task<CsatTemplateEntry?> GetByIdAsync(TenantId tenantId, EntityId templateId, CancellationToken ct);

    /// <summary>
    /// Returns the templates for a tenant matching a <paramref name="channel"/> and
    /// <paramref name="locale"/>. Used by the provider fallback chain — a tenant-locale
    /// hit short-circuits, otherwise the caller widens the query. Ordered
    /// <c>is_default DESC</c> so a default row is preferred within a match set.
    /// </summary>
    Task<IReadOnlyList<CsatTemplateEntry>> GetByChannelAndLocaleAsync(
        TenantId tenantId, string channel, string locale, CancellationToken ct);

    /// <summary>
    /// Returns the templates for a tenant matching a <paramref name="channel"/> whose
    /// <c>is_default</c> is <c>true</c> (the tenant's default-locale set), ordered by locale
    /// for deterministic fallback selection.
    /// </summary>
    Task<IReadOnlyList<CsatTemplateEntry>> GetDefaultsByChannelAsync(
        TenantId tenantId, string channel, CancellationToken ct);

    /// <summary>Inserts or updates a template (upsert on <c>(tenant_id, template_id)</c>).</summary>
    Task SaveAsync(CsatTemplateEntry entry, CancellationToken ct);

    /// <summary>Deletes a template by id (no-op when absent).</summary>
    Task DeleteAsync(TenantId tenantId, EntityId templateId, CancellationToken ct);
}
