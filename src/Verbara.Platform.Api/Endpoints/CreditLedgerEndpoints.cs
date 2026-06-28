using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// c1 (credit-ledger-topups, ADR-0033 (c) addendum) — the sellable half of the AI-credit ledger:
/// an operator top-up mint plus the tenant-facing balance/entries read API. Top-ups are fungible
/// <see cref="CreditSource.TopUp"/> grants minted via the shipped, idempotent
/// <see cref="ICreditLedgerStore.PostGrantAsync"/>; the money path (<c>PostMeteredDebitAsync</c> /
/// invoice / quota) is deliberately untouched — a top-up simply raises the prepaid balance that the
/// existing covered/PostPaid split spends correctly.
/// </summary>
internal static class CreditLedgerEndpoints
{
    /// <summary>
    /// Double-lock policy for the top-up mint: the host/partner-tenant gate AND the seeded
    /// <c>billing:credits:grant</c> permission, enforced against BOTH a management key's scopes and a
    /// user JWT's resolved permissions (registered in <c>Program.cs</c> via
    /// <c>PlatformAdminRequirement("billing:credits:grant")</c>). A bare
    /// <c>.RequireAuthorization("billing:credits:grant")</c> would NOT enforce the scope on the
    /// management-key path (the Admin-role shortcut auto-succeeds) — minting credits is money creation, so
    /// the permission must be a real gate, mirroring MfaAdmin/AuditAdmin.
    /// </summary>
    public const string GrantPolicy = "CreditGrantGate";

    public static void MapCreditLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Operator mint surface (cross-tenant; tenant carried in the body) ──
        var mg = app.MapGroup("/management/credit-ledger").RequireAuthorization("PlatformAdminOnly");
        mg.MapPost("/top-up", TopUp).RequireAuthorization(GrantPolicy);
        mg.MapPost("/promo-grant", PromoGrant).RequireAuthorization(GrantPolicy);
        mg.MapPost("/partner-grant", PartnerGrant).RequireAuthorization(GrantPolicy);

        // ── Tenant-facing read surface (scoped to the resolved operational tenant) ──
        var tg = app.MapGroup("/admin/credit-ledger")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant();
        tg.MapGet("/balance", GetBalance).RequireAuthorization("billing:credits:read");
        tg.MapGet("/entries", GetEntries).RequireAuthorization("billing:credits:read");
        tg.MapGet("/remaining-by-source", GetRemainingBySource).RequireAuthorization("billing:credits:read");

        // ── Partner-facing derive-on-read attribution (scoped to the caller's Partner tenant) ──
        var pg = app.MapGroup("/partner/credit-ledger").RequireAuthorization("PartnerAdminOnly");
        pg.MapGet("/attribution", GetPartnerAttribution);
    }

    // ─── Operator mint ────────────────────────────────────────────────────────────

    private static async Task<IResult> TopUp(
        [FromBody] TopUpRequest req,
        [FromServices] ICreditLedgerStore ledger,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        if (req.Amount <= 0m)
            return Results.BadRequest(new ErrorResponse("Amount must be positive."));
        if (string.IsNullOrWhiteSpace(req.TenantId))
            return Results.BadRequest(new ErrorResponse("TenantId is required."));
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
            return Results.BadRequest(new ErrorResponse("IdempotencyKey is required."));

        var tenantId = new TenantId(req.TenantId);

        // Fungible TopUp grant via the shipped, idempotent PostGrantAsync (dedupes on external_ref):
        // a repeat with the same IdempotencyKey is a no-op that neither double-inserts nor double-credits.
        var entry = new CreditLedgerEntry
        {
            EntryId = EntityId.New(),
            TenantId = tenantId,
            EntryType = CreditEntryType.Grant,
            Source = CreditSource.TopUp,
            Amount = req.Amount,
            ExternalRef = req.IdempotencyKey,
            CreatedAt = clock.UtcNow,
        };

        await ledger.PostGrantAsync(entry, ct);

        var balance = await ledger.GetBalanceAsync(tenantId, ct);
        return Results.Ok(new CreditBalanceResponse(balance));
    }

    private static async Task<IResult> PromoGrant(
        [FromBody] PromoGrantRequest req,
        [FromServices] ICreditLedgerStore ledger,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        if (req.Amount <= 0m)
            return Results.BadRequest(new ErrorResponse("Amount must be positive."));
        if (string.IsNullOrWhiteSpace(req.TenantId))
            return Results.BadRequest(new ErrorResponse("TenantId is required."));
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
            return Results.BadRequest(new ErrorResponse("IdempotencyKey is required."));

        var tenantId = new TenantId(req.TenantId);

        // Promotional grant — a (possibly-expiring) Promo lot drawn FIFO-first. Idempotent on external_ref via the
        // shipped PostGrantAsync: a repeat with the same IdempotencyKey neither double-inserts nor double-credits.
        var entry = new CreditLedgerEntry
        {
            EntryId = EntityId.New(),
            TenantId = tenantId,
            EntryType = CreditEntryType.Grant,
            Source = CreditSource.Promo,
            Amount = req.Amount,
            ExpiresAt = req.ExpiresAt,
            ExternalRef = req.IdempotencyKey,
            CreatedAt = clock.UtcNow,
        };

        await ledger.PostGrantAsync(entry, ct);

        var balance = await ledger.GetBalanceAsync(tenantId, ct);
        return Results.Ok(new CreditBalanceResponse(balance));
    }

    private static async Task<IResult> PartnerGrant(
        [FromBody] PartnerGrantRequest req,
        [FromServices] ICreditLedgerStore ledger,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        if (req.Amount <= 0m)
            return Results.BadRequest(new ErrorResponse("Amount must be positive."));
        if (string.IsNullOrWhiteSpace(req.TenantId))
            return Results.BadRequest(new ErrorResponse("TenantId is required."));
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
            return Results.BadRequest(new ErrorResponse("IdempotencyKey is required."));

        // Partner grants are only valid for a tenant whose parent is a Partner — the single-hop
        // ParentTenantId + parent.Type == TenantType.Partner gate (ManagementTenantEndpoints.cs pattern), so the
        // partner-funded draws stay attributable to the owning partner with no Tenant-model change.
        var customer = await tenantStore.GetAsync(req.TenantId, ct);
        if (customer is null)
            return Results.NotFound(new ErrorResponse($"Tenant '{req.TenantId}' not found."));
        if (string.IsNullOrEmpty(customer.ParentTenantId))
            return Results.BadRequest(new ErrorResponse("Partner grants require a tenant whose parent is a Partner."));

        var parent = await tenantStore.GetAsync(customer.ParentTenantId, ct);
        if (parent?.Type != TenantType.Partner)
            return Results.BadRequest(new ErrorResponse("Partner grants require a tenant whose parent is a Partner."));

        var tenantId = new TenantId(req.TenantId);

        // Partner allocation grant — a drawable Partner lot drawn before Subscription/TopUp; its draws are tagged
        // CreditSource.Partner (never PostPaid) so they never enter the customer-owed invoice. Idempotent on external_ref.
        var entry = new CreditLedgerEntry
        {
            EntryId = EntityId.New(),
            TenantId = tenantId,
            EntryType = CreditEntryType.Grant,
            Source = CreditSource.Partner,
            Amount = req.Amount,
            ExternalRef = req.IdempotencyKey,
            CreatedAt = clock.UtcNow,
        };

        await ledger.PostGrantAsync(entry, ct);

        var balance = await ledger.GetBalanceAsync(tenantId, ct);
        return Results.Ok(new CreditBalanceResponse(balance));
    }

    // ─── Tenant read ──────────────────────────────────────────────────────────────

    private static async Task<IResult> GetBalance(
        HttpContext context,
        [FromServices] ICreditLedgerStore ledger,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var balance = await ledger.GetBalanceAsync(tenantId, ct);
        return Results.Ok(new CreditBalanceResponse(balance));
    }

    private static async Task<IResult> GetEntries(
        HttpContext context,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromServices] ICreditLedgerStore ledger,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        // Clamp paging (route defaults page=1/pageSize=25 via the binding below are overridden when
        // the caller passes 0/negative/oversized values).
        if (page < 1)
            page = 1;
        if (pageSize <= 0)
            pageSize = 25;
        else if (pageSize > 200)
            pageSize = 200;

        var items = await ledger.GetEntriesAsync(tenantId, page, pageSize, ct);
        var total = await ledger.GetEntriesCountAsync(tenantId, ct);

        return Results.Ok(new PagedResult<CreditLedgerEntryDto>(
            items.Select(MapEntry).ToList(), total, page, pageSize));
    }

    private static async Task<IResult> GetRemainingBySource(
        HttpContext context,
        [FromServices] ICreditLedgerStore ledger,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var rows = await ledger.GetRemainingBySourceAsync(tenantId, clock.UtcNow, ct);

        // Surface the CreditSource enum as its name (mirrors MapEntry). Σ Remaining == GetBalanceAsync (the
        // open non-expired lot projection re-derives the O(1) balance — excludes expired Promo + PostPaid).
        var dtos = rows
            .Select(static r => new SourceRemainingDto(r.Source.ToString(), r.Remaining))
            .ToList();

        return Results.Ok(new SourceRemainingResponse(dtos));
    }

    // ─── Partner derive-on-read attribution ───────────────────────────────────────

    private static async Task<IResult> GetPartnerAttribution(
        HttpContext context,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromServices] ICreditLedgerStore ledger,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        // The caller's OWN Partner tenant id, read from the VERIFIED JWT claim — the exact source
        // PartnerAdminAuthorizationHandler gated on. NOT Items["TenantId"]: TenantBoundaryValidationMiddleware
        // permits a Partner to set X-Tenant-Id to another tenant, so reading the resolved tenant here would let a
        // Partner aggregate another partner's attribution. The verified claim cannot be overridden.
        var partnerTenantId = GetVerifiedPartnerTenantId(context);

        var now = clock.UtcNow;
        var periodStart = from ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = to ?? now;

        return Results.Ok(await AggregatePartnerAttributionAsync(
            partnerTenantId, periodStart, periodEnd, ledger, tenantStore, ct));
    }

    /// <summary>
    /// Derive-on-read partner attribution (ADR-0033 (c2) addendum): for each direct <see cref="TenantType.Customer"/>
    /// child of the partner, sums <c>Σ |Partner-source debits|</c> in the half-open <c>[periodStart, periodEnd)</c>
    /// window via <see cref="ICreditLedgerStore.GetPartnerSourceDebitsTotalAsync"/>. No invoice and no materialized
    /// table — the attribution is computed from the ledger + the existing single-hop tenant hierarchy on demand. The
    /// cross-tenant aggregation lives in the Api layer (where <see cref="ITenantStore"/> is wired) because
    /// <c>Verbara.Platform.Billing</c> does not reference Pro.MultiTenant (the open-core boundary).
    /// </summary>
    internal static async Task<PartnerAttributionResponse> AggregatePartnerAttributionAsync(
        TenantId partnerTenantId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        ICreditLedgerStore ledger,
        ITenantStore tenantStore,
        CancellationToken ct)
    {
        var children = await tenantStore.GetChildrenAsync(partnerTenantId.Value, ct);

        var lines = new List<PartnerAttributionLineDto>();
        var total = 0m;
        foreach (var child in children)
        {
            if (child.Type != TenantType.Customer)
                continue;

            var credits = await ledger.GetPartnerSourceDebitsTotalAsync(
                new TenantId(child.TenantId), periodStart, periodEnd, ct);
            lines.Add(new PartnerAttributionLineDto(child.TenantId, credits));
            total += credits;
        }

        return new PartnerAttributionResponse(total, lines);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private static TenantId GetTenantId(HttpContext context)
    {
        // The resolved operational tenant. TenantResolutionMiddleware writes Items["TenantId"] as a TenantId
        // struct on the JWT path and as a string on the X-Tenant-Id-header / subdomain path — accept both
        // (these read endpoints sit behind AdminOnly + RequireOperationalTenant; cross-tenant override is gated
        // upstream by TenantBoundaryValidationMiddleware).
        if (context.Items.TryGetValue("TenantId", out var val))
        {
            if (val is TenantId tid)
                return tid;
            if (val is string s && !string.IsNullOrEmpty(s))
                return new TenantId(s);
        }

        throw new InvalidOperationException("Tenant ID not resolved");
    }

    /// <summary>
    /// The caller's OWN partner tenant id, read from the VERIFIED JWT claim (<c>tenant_id</c>/<c>tid</c>) — the
    /// exact source <c>PartnerAdminAuthorizationHandler</c> gated on. This deliberately does NOT use the resolved
    /// <c>Items["TenantId"]</c>: <c>TenantBoundaryValidationMiddleware</c> permits a Partner to set
    /// <c>X-Tenant-Id</c> to another tenant, so reading the resolved tenant here would let a Partner aggregate
    /// another partner's attribution. The verified claim cannot be overridden.
    /// </summary>
    private static TenantId GetVerifiedPartnerTenantId(HttpContext context)
    {
        var claim = context.User.FindFirst("tenant_id")?.Value
            ?? context.User.FindFirst("tid")?.Value;
        if (string.IsNullOrEmpty(claim))
            throw new InvalidOperationException("Partner tenant claim not present");
        return new TenantId(claim);
    }

    private static CreditLedgerEntryDto MapEntry(CreditLedgerEntry e) => new(
        e.EntryId.Value,
        e.EntryType.ToString(),
        e.Source.ToString(),
        e.Amount,
        e.ExternalRef,
        e.ExpiresAt,
        e.CreatedAt);
}

/// <summary>
/// c1 — operator top-up request body. Mints a fungible <see cref="CreditSource.TopUp"/> grant for
/// <paramref name="TenantId"/> of <paramref name="Amount"/> credits, idempotent on
/// <paramref name="IdempotencyKey"/> (→ the ledger entry's <c>external_ref</c>).
/// </summary>
/// <param name="TenantId">Target tenant the credits are minted onto.</param>
/// <param name="Amount">Positive credit amount to grant.</param>
/// <param name="IdempotencyKey">Caller-supplied idempotency key; a repeat is a no-op.</param>
internal sealed record TopUpRequest(string TenantId, decimal Amount, string IdempotencyKey);

/// <summary>c1 — the tenant's current O(1) AI-credit balance projection.</summary>
/// <param name="Balance">Current credit balance (never negative).</param>
internal sealed record CreditBalanceResponse(decimal Balance);

/// <summary>
/// c1 — a single AI-credit ledger entry projected for the tenant read API. The domain
/// <see cref="CreditLedgerEntry"/> is never serialized directly; enums are surfaced as their names.
/// </summary>
/// <param name="EntryId">Ledger entry id (EntityId hex).</param>
/// <param name="EntryType"><c>Grant</c> or <c>Debit</c>.</param>
/// <param name="Source">Economic source (<c>Subscription</c>, <c>TopUp</c>, <c>Promo</c>, <c>Partner</c>, <c>PostPaid</c>).</param>
/// <param name="Amount">Signed credit amount (positive for grants, negative for debits).</param>
/// <param name="ExternalRef">Top-up idempotency key, if any.</param>
/// <param name="ExpiresAt">When this (grant) lot expires, if any.</param>
/// <param name="CreatedAt">When the entry was appended (UTC).</param>
internal sealed record CreditLedgerEntryDto(
    string EntryId,
    string EntryType,
    string Source,
    decimal Amount,
    string? ExternalRef,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// c2 (credit-ledger-lots) — operator promotional grant body. Mints a <see cref="CreditSource.Promo"/> grant +
/// lot for <paramref name="TenantId"/> of <paramref name="Amount"/> credits, optionally expiring at
/// <paramref name="ExpiresAt"/>, idempotent on <paramref name="IdempotencyKey"/> (→ the entry's <c>external_ref</c>).
/// </summary>
/// <param name="TenantId">Target tenant the promo credits are minted onto.</param>
/// <param name="Amount">Positive credit amount to grant.</param>
/// <param name="IdempotencyKey">Caller-supplied idempotency key; a repeat is a no-op.</param>
/// <param name="ExpiresAt">Optional lot expiry; the reclaim sweeper claws back the unconsumed remainder after this.</param>
internal sealed record PromoGrantRequest(string TenantId, decimal Amount, string IdempotencyKey, DateTimeOffset? ExpiresAt);

/// <summary>
/// c2 (credit-ledger-lots) — operator partner-allocation grant body. Mints a <see cref="CreditSource.Partner"/>
/// grant + lot for <paramref name="TenantId"/> (whose parent MUST be a <c>Partner</c>) of <paramref name="Amount"/>
/// credits, idempotent on <paramref name="IdempotencyKey"/>. Partner draws never enter the customer-owed invoice.
/// </summary>
/// <param name="TenantId">Target Customer tenant (parent must be a Partner) the credits are minted onto.</param>
/// <param name="Amount">Positive credit amount to grant.</param>
/// <param name="IdempotencyKey">Caller-supplied idempotency key; a repeat is a no-op.</param>
internal sealed record PartnerGrantRequest(string TenantId, decimal Amount, string IdempotencyKey);

/// <summary>
/// c2 — open (drawable) remaining credit for one <see cref="CreditSource"/>, surfaced as the enum name. The Σ of
/// <paramref name="Remaining"/> over a <see cref="SourceRemainingResponse"/> reconciles to the tenant's balance.
/// </summary>
/// <param name="Source">Economic source name (<c>Subscription</c>, <c>TopUp</c>, <c>Promo</c>, <c>Partner</c>).</param>
/// <param name="Remaining">Open, non-expired remaining credit for this source.</param>
internal sealed record SourceRemainingDto(string Source, decimal Remaining);

/// <summary>c2 — the per-source open-remaining breakdown for the resolved tenant (Σ == balance).</summary>
/// <param name="Sources">One line per source with open remaining; sources with zero open remaining are omitted.</param>
internal sealed record SourceRemainingResponse(IReadOnlyList<SourceRemainingDto> Sources);

/// <summary>
/// c2 — one partner-attribution line: the partner-funded AI-credit consumption attributable to a single Customer
/// child for the window, derived on read from the ledger (no invoice, no materialized table).
/// </summary>
/// <param name="CustomerTenantId">The Customer child tenant id.</param>
/// <param name="Credits">Σ <c>|Partner-source debits|</c> for this customer in the window.</param>
internal sealed record PartnerAttributionLineDto(string CustomerTenantId, decimal Credits);

/// <summary>
/// c2 — derive-on-read partner attribution for the caller's Partner tenant over a window: the total partner-funded
/// consumption plus the per-Customer breakdown. Computed from <see cref="ICreditLedgerStore"/> + the existing
/// single-hop tenant hierarchy — no invoice and no materialized aggregation table (ADR-0033 (c2) addendum).
/// </summary>
/// <param name="Total">Σ of the per-customer <see cref="PartnerAttributionLineDto.Credits"/>.</param>
/// <param name="Customers">One line per direct Customer child with attributable partner-funded consumption.</param>
internal sealed record PartnerAttributionResponse(decimal Total, IReadOnlyList<PartnerAttributionLineDto> Customers);
