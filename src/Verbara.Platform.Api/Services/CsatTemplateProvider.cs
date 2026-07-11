using Verbara.Platform.Core;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Pro.CsatRunner.Contracts;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Platform's in-process implementation of the Pro-defined
/// <see cref="ICsatTemplateProvider"/> contract (csat-runner Phase E, Platform/ADR-0020 +
/// verbara-meta/ADR-0005 open-core boundary). Pro's channel adapters call
/// <see cref="GetTemplateAsync"/> via DI — same process, NOT an API call — to fetch a
/// locale-templated CSAT prompt, and Pro depends only on the opaque contract name, never
/// a Platform internal. Backed by the <see cref="ICsatTemplateStore"/> over
/// <c>csat_templates</c>.
/// </summary>
/// <remarks>
/// Resolution follows the fallback chain
/// <c>tenant-locale → tenant-default-locale → global-default-locale → global-default-en-US</c>
/// (design §D4). Because default templates are seeded per tenant on create
/// (<c>TenantProvisioningService</c>, task 5.3) using <see cref="CsatDefaultTemplates"/>, the
/// terminal <c>global-default-en-US</c> step always finds a row for a templatable channel —
/// so the returned <see cref="CsatTemplate"/> is non-null whenever a template exists for the
/// channel. Webchat carries no per-tenant template (Web owns the i18n strings); a webchat
/// request resolves nothing and returns <c>null</c>.
/// </remarks>
internal sealed class CsatTemplateProvider : ICsatTemplateProvider
{
    private readonly ICsatTemplateStore _store;

    public CsatTemplateProvider(ICsatTemplateStore store) => _store = store;

    public async ValueTask<CsatTemplate?> GetTemplateAsync(
        string tenantId, string channel, string locale, CancellationToken cancellationToken = default)
    {
        var tenant = new TenantId(tenantId);

        // 1 & 2 — tenant-locale, then tenant-default-locale (default row for the requested
        // locale). GetByChannelAndLocaleAsync orders is_default DESC, so an exact non-default
        // row wins over the default of the same locale; when only a default exists it is used.
        var exact = await _store.GetByChannelAndLocaleAsync(tenant, channel, locale, cancellationToken)
            .ConfigureAwait(false);
        if (exact.Count > 0)
            return Map(exact[0]);

        // 3 — global-default-locale: the tenant's seeded default set. Prefer a default row for
        // the requested locale, else fall to 4.
        var defaults = await _store.GetDefaultsByChannelAsync(tenant, channel, cancellationToken)
            .ConfigureAwait(false);
        if (defaults.Count == 0)
            return null;

        var localeDefault = defaults.FirstOrDefault(
            d => string.Equals(d.Locale, locale, StringComparison.OrdinalIgnoreCase));
        if (localeDefault is not null)
            return Map(localeDefault);

        // 4 — global-default-en-US: the terminal fallback.
        var enUsDefault = defaults.FirstOrDefault(
            d => string.Equals(d.Locale, CsatDefaultTemplates.GlobalDefaultLocale, StringComparison.OrdinalIgnoreCase));
        if (enUsDefault is not null)
            return Map(enUsDefault);

        // No en-US default configured — return the first default deterministically so a
        // resolvable channel never yields null when any default exists.
        return Map(defaults[0]);
    }

    // Store entity → Pro's opaque return type. Locale reports the locale actually resolved by
    // the fallback chain (may differ from the requested locale), per the CsatTemplate contract.
    private static CsatTemplate Map(CsatTemplateEntry entry) => new()
    {
        Subject = entry.Subject,
        Body = entry.Body,
        Locale = entry.Locale,
    };
}
