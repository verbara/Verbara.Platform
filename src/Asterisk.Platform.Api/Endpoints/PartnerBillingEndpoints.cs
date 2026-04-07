using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class PartnerBillingEndpoints
{
    public static RouteGroupBuilder MapPartnerBillingEndpoints(this RouteGroupBuilder app)
    {
        var group = app.MapGroup("/partner")
            .WithTags("Partner - Billing")
            .RequireAuthorization("PartnerAdminOnly");

        group.MapGet("/rate-cards", ListRateCards)
            .RequireAuthorization("partner:billing:manage");
        group.MapPost("/rate-cards", CreateRateCard)
            .RequireAuthorization("partner:billing:manage");
        group.MapPut("/rate-cards/{rateCardId}", UpdateRateCard)
            .RequireAuthorization("partner:billing:manage");
        group.MapDelete("/rate-cards/{rateCardId}", DeleteRateCard)
            .RequireAuthorization("partner:billing:manage");

        group.MapGet("/customers/{customerId}/invoices", ListCustomerInvoices)
            .RequireAuthorization("partner:billing:view");
        group.MapPost("/customers/{customerId}/invoices/generate", GenerateCustomerInvoice)
            .RequireAuthorization("partner:billing:manage");
        group.MapGet("/customers/{customerId}/usage", GetCustomerUsage)
            .RequireAuthorization("partner:billing:view");

        return app;
    }

    // ─── Rate Card Handlers ──────────────────────────────────────────────────────

    private static async Task<IResult> ListRateCards(
        HttpContext context,
        [FromServices] IRateCardStore rateCardStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var cards = await rateCardStore.ListAsync(new TenantId(callerTenantId), ct);
        return Results.Ok(cards.Select(MapRateCardToDto).ToList());
    }

    private static async Task<IResult> CreateRateCard(
        HttpContext context,
        [FromBody] CreateRateCardRequest body,
        [FromServices] IRateCardStore rateCardStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var rateCard = new RateCard
        {
            RateCardId = EntityId.New(),
            TenantId = new TenantId(callerTenantId),
            Name = body.Name,
            Currency = body.Currency,
            EffectiveFrom = body.EffectiveFrom,
            EffectiveTo = body.EffectiveTo,
            IsDefault = body.IsDefault,
            Rates = body.Rates.Select(MapDtoToRateEntry).ToList(),
        };

        await rateCardStore.SaveAsync(rateCard, ct);
        return Results.Created($"/partner/rate-cards/{rateCard.RateCardId.Value}", MapRateCardToDto(rateCard));
    }

    private static async Task<IResult> UpdateRateCard(
        string rateCardId,
        HttpContext context,
        [FromBody] CreateRateCardRequest body,
        [FromServices] IRateCardStore rateCardStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var partnerTid = new TenantId(callerTenantId);
        var existing = await rateCardStore.GetByIdAsync(partnerTid, EntityId.From(rateCardId), ct);
        if (existing is null)
            return Results.NotFound();

        var updated = new RateCard
        {
            RateCardId = existing.RateCardId,
            TenantId = existing.TenantId,
            Name = body.Name,
            Currency = body.Currency,
            EffectiveFrom = body.EffectiveFrom,
            EffectiveTo = body.EffectiveTo,
            IsDefault = body.IsDefault,
            Rates = body.Rates.Select(MapDtoToRateEntry).ToList(),
        };

        await rateCardStore.SaveAsync(updated, ct);
        return Results.Ok(MapRateCardToDto(updated));
    }

    private static async Task<IResult> DeleteRateCard(
        string rateCardId,
        HttpContext context,
        [FromServices] IRateCardStore rateCardStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var partnerTid = new TenantId(callerTenantId);
        var existing = await rateCardStore.GetByIdAsync(partnerTid, EntityId.From(rateCardId), ct);
        if (existing is null)
            return Results.NotFound();

        await rateCardStore.DeleteAsync(partnerTid, EntityId.From(rateCardId), ct);
        return Results.NoContent();
    }

    // ─── Customer Invoice Handlers ───────────────────────────────────────────────

    private static async Task<IResult> ListCustomerInvoices(
        string customerId,
        HttpContext context,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IInvoiceStore invoiceStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        var invoices = await invoiceStore.ListAsync(new TenantId(customerId), page ?? 1, pageSize ?? 20, ct);
        return Results.Ok(invoices.Select(MapInvoiceToDto).ToList());
    }

    private static async Task<IResult> GenerateCustomerInvoice(
        string customerId,
        HttpContext context,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IRateCardStore rateCardStore,
        [FromServices] IInvoiceGenerationService invoiceService,
        [FromServices] IInvoiceStore invoiceStore,
        [FromServices] IPartnerRevenueStore revenueStore,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        // 1. Validate ownership
        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        var now = clock.UtcNow;
        var periodStart = from ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = to ?? now;

        var partnerTid = new TenantId(callerTenantId);
        var customerTid = new TenantId(customerId);

        // 2. Load partner's active rate card
        var partnerRateCard = await rateCardStore.GetActiveAsync(partnerTid, now, ct);
        if (partnerRateCard is null)
            return Results.BadRequest(new ErrorResponse("No active rate card configured for this partner."));

        // 3. Generate invoice for customer using partner's rate card
        Invoice invoice;
        try
        {
            invoice = await invoiceService.GenerateWithRateCardAsync(customerTid, partnerRateCard, periodStart, periodEnd, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }

        // 4. Save the invoice
        await invoiceStore.SaveAsync(invoice, ct);

        // 5. Load platform base rate card from host tenant
        var hostTenant = await tenantStore.GetHostTenantAsync(ct);
        if (hostTenant is null)
            return Results.BadRequest(new ErrorResponse("Platform host tenant not found."));

        var hostTid = new TenantId(hostTenant.TenantId);
        var baseRateCard = await rateCardStore.GetActiveAsync(hostTid, now, ct);
        if (baseRateCard is null)
            return Results.BadRequest(new ErrorResponse("No platform base rate card configured."));

        // 6. Calculate platform cost (not saved)
        Invoice baseInvoice;
        try
        {
            baseInvoice = await invoiceService.GenerateWithRateCardAsync(customerTid, baseRateCard, periodStart, periodEnd, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }

        // 7 & 8. Create and save PartnerRevenueRecord
        var revenueRecord = new PartnerRevenueRecord
        {
            RevenueId = EntityId.New(),
            PartnerTenantId = partnerTid,
            CustomerTenantId = customerTid,
            InvoiceId = invoice.InvoiceId,
            GrossAmount = invoice.Total,
            PlatformCost = baseInvoice.Total,
            PartnerMargin = invoice.Total - baseInvoice.Total,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            CreatedAt = now,
        };

        await revenueStore.UpsertAsync(revenueRecord, ct);

        // 9. Return response
        var revenueSnapshot = new PartnerRevenueSnapshotDto(
            revenueRecord.GrossAmount,
            revenueRecord.PlatformCost,
            revenueRecord.PartnerMargin);

        return Results.Created(
            $"/partner/customers/{customerId}/invoices/{invoice.InvoiceId.Value}",
            new PartnerGenerateInvoiceResponse(MapInvoiceToDto(invoice), revenueSnapshot));
    }

    // ─── Customer Usage Handler ──────────────────────────────────────────────────

    private static async Task<IResult> GetCustomerUsage(
        string customerId,
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IMeteringService meteringService,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        var summaries = await meteringService.GetCurrentPeriodSummaryAsync(new TenantId(customerId), ct);
        return Results.Ok(summaries.Select(MapSummaryToDto).ToList());
    }

    // ─── Mapping Helpers ─────────────────────────────────────────────────────────

    private static RateCardDto MapRateCardToDto(RateCard rc) => new(
        rc.RateCardId.Value, rc.TenantId.Value, rc.Name, rc.Currency,
        rc.EffectiveFrom, rc.EffectiveTo, rc.IsDefault,
        rc.Rates.Select(MapRateEntryToDto).ToList());

    private static RateEntryDto MapRateEntryToDto(RateEntry re) => new(
        re.UsageType.ToString(), re.UnitPrice, re.IncludedQuantity,
        re.Tiers?.Select(t => new RateTierDto(t.FromQuantity, t.ToQuantity, t.UnitPrice)).ToList());

    private static RateEntry MapDtoToRateEntry(RateEntryDto dto) => new()
    {
        UsageType = Enum.Parse<UsageType>(dto.UsageType),
        UnitPrice = dto.UnitPrice,
        IncludedQuantity = dto.IncludedQuantity,
        Tiers = dto.Tiers?.Select(t => new RateTier
        {
            FromQuantity = t.FromQuantity,
            ToQuantity = t.ToQuantity,
            UnitPrice = t.UnitPrice,
        }).ToList(),
    };

    private static InvoiceDto MapInvoiceToDto(Invoice inv) => new(
        inv.InvoiceId.Value, inv.TenantId.Value, inv.PeriodStart, inv.PeriodEnd,
        inv.Currency, inv.LineItems.Select(MapLineItemToDto).ToList(),
        inv.Subtotal, inv.Tax, inv.Total, inv.Status.ToString(),
        inv.GeneratedAt, inv.IssuedAt, inv.PaidAt,
        inv.PaymentStatus.ToString(), inv.DueDate);

    private static InvoiceLineItemDto MapLineItemToDto(InvoiceLineItem li) => new(
        li.UsageType.ToString(), li.Description, li.Quantity, li.UnitPrice,
        li.Amount, li.IncludedQuantity, li.OverageQuantity);

    private static UsageSummaryDto MapSummaryToDto(UsageSummary s) => new(
        s.UsageType.ToString(), s.TotalQuantity, s.RecordCount,
        s.PeriodStart, s.PeriodEnd, s.LastUpdatedAt);
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record PartnerGenerateInvoiceResponse(InvoiceDto Invoice, PartnerRevenueSnapshotDto Revenue);

internal sealed record PartnerRevenueSnapshotDto(decimal GrossAmount, decimal PlatformCost, decimal PartnerMargin);
