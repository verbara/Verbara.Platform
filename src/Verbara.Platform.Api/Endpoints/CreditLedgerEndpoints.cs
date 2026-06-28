using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
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
    public static void MapCreditLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Operator mint surface (cross-tenant; tenant carried in the body) ──
        var mg = app.MapGroup("/management/credit-ledger").RequireAuthorization("PlatformAdminOnly");
        mg.MapPost("/top-up", TopUp).RequireAuthorization("billing:credits:grant");

        // ── Tenant-facing read surface (scoped to the resolved operational tenant) ──
        var tg = app.MapGroup("/admin/credit-ledger")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant();
        tg.MapGet("/balance", GetBalance).RequireAuthorization("billing:credits:read");
        tg.MapGet("/entries", GetEntries).RequireAuthorization("billing:credits:read");
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

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
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
