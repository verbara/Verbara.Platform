using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementBillingEndpoints
{
    public static void MapManagementBillingEndpoints(this IEndpointRouteBuilder app)
    {
        // Rate Cards
        var rc = app.MapGroup("/management/rate-cards").RequireAuthorization("PlatformAdminOnly");
        rc.MapGet("/", ListRateCards);
        rc.MapPost("/", CreateRateCard);
        rc.MapPut("/{id}", UpdateRateCard);
        rc.MapDelete("/{id}", DeleteRateCard);

        // Invoices
        var inv = app.MapGroup("/management/invoices").RequireAuthorization("PlatformAdminOnly");
        inv.MapGet("/", ListInvoices);
        inv.MapPost("/generate", GenerateInvoice);
        inv.MapGet("/{id}", GetInvoice);
        inv.MapPost("/{id}/issue", IssueInvoice);
        inv.MapPost("/{invoiceId}/pay", PayInvoice);

        // Dunning
        var dunning = app.MapGroup("/management/tenants/{id}")
            .RequireAuthorization("PlatformAdminOnly");
        dunning.MapGet("/dunning", GetDunning);
        dunning.MapPost("/dunning/pause", PauseDunning);
        dunning.MapPost("/dunning/resume", ResumeDunning);

        // Usage & Quotas (per-tenant)
        var tb = app.MapGroup("/management/tenants/{tenantId}").RequireAuthorization("PlatformAdminOnly");
        tb.MapGet("/usage", GetUsageSummary);
        tb.MapGet("/usage/details", GetUsageDetails);
        tb.MapGet("/quota", GetQuotaStatus);
        tb.MapPut("/quota", UpdateQuota);
    }

    // ─── Rate Card Handlers ──────────────────────────────────────────────────────

    private static async Task<IResult> ListRateCards(
        [FromQuery] string tenantId,
        [FromServices] IRateCardStore store,
        CancellationToken ct)
    {
        var cards = await store.ListAsync(new TenantId(tenantId), ct);
        return Results.Ok(cards.Select(MapRateCardToDto).ToList());
    }

    private static async Task<IResult> CreateRateCard(
        [FromQuery] string tenantId,
        [FromBody] CreateRateCardRequest body,
        [FromServices] IRateCardStore store,
        CancellationToken ct)
    {
        var rateCard = new RateCard
        {
            RateCardId = EntityId.New(),
            TenantId = new TenantId(tenantId),
            Name = body.Name,
            Currency = body.Currency,
            EffectiveFrom = body.EffectiveFrom,
            EffectiveTo = body.EffectiveTo,
            IsDefault = body.IsDefault,
            Rates = body.Rates.Select(MapDtoToRateEntry).ToList(),
        };

        await store.SaveAsync(rateCard, ct);
        return Results.Created($"/management/rate-cards/{rateCard.RateCardId.Value}", MapRateCardToDto(rateCard));
    }

    private static async Task<IResult> UpdateRateCard(
        string id,
        [FromQuery] string tenantId,
        [FromBody] CreateRateCardRequest body,
        [FromServices] IRateCardStore store,
        CancellationToken ct)
    {
        var tid = new TenantId(tenantId);
        var existing = await store.GetByIdAsync(tid, EntityId.From(id), ct);
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

        await store.SaveAsync(updated, ct);
        return Results.Ok(MapRateCardToDto(updated));
    }

    private static async Task<IResult> DeleteRateCard(
        string id,
        [FromQuery] string tenantId,
        [FromServices] IRateCardStore store,
        CancellationToken ct)
    {
        await store.DeleteAsync(new TenantId(tenantId), EntityId.From(id), ct);
        return Results.NoContent();
    }

    // ─── Invoice Handlers ────────────────────────────────────────────────────────

    private static async Task<IResult> ListInvoices(
        [FromQuery] string tenantId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] IInvoiceStore store,
        CancellationToken ct)
    {
        var invoices = await store.ListAsync(new TenantId(tenantId), page ?? 1, pageSize ?? 20, ct);
        return Results.Ok(invoices.Select(MapInvoiceToDto).ToList());
    }

    private static async Task<IResult> GenerateInvoice(
        [FromQuery] string tenantId,
        [FromBody] GenerateInvoiceRequest body,
        [FromServices] IInvoiceGenerationService generator,
        [FromServices] IInvoiceStore store,
        CancellationToken ct)
    {
        Invoice invoice;
        try
        {
            invoice = await generator.GenerateAsync(new TenantId(tenantId), body.PeriodStart, body.PeriodEnd, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }

        await store.SaveAsync(invoice, ct);
        return Results.Created($"/management/invoices/{invoice.InvoiceId.Value}", MapInvoiceToDto(invoice));
    }

    private static async Task<IResult> GetInvoice(
        string id,
        [FromQuery] string tenantId,
        [FromServices] IInvoiceStore store,
        CancellationToken ct)
    {
        var invoice = await store.GetByIdAsync(new TenantId(tenantId), EntityId.From(id), ct);
        return invoice is null ? Results.NotFound() : Results.Ok(MapInvoiceToDto(invoice));
    }

    private static async Task<IResult> IssueInvoice(
        string id,
        [FromQuery] string tenantId,
        [FromServices] IInvoiceStore store,
        CancellationToken ct)
    {
        var invoice = await store.GetByIdAsync(new TenantId(tenantId), EntityId.From(id), ct);
        if (invoice is null)
            return Results.NotFound();

        await store.UpdateStatusAsync(new TenantId(tenantId), EntityId.From(id), InvoiceStatus.Issued, ct);
        return Results.Ok(new StatusUpdateResponse(id, "Issued"));
    }

    // ─── Usage Handlers ──────────────────────────────────────────────────────────

    private static async Task<IResult> GetUsageSummary(
        string tenantId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? until,
        [FromServices] IUsageRecordStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        var effectiveFrom = from ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var effectiveUntil = until ?? now;

        var summaries = await store.GetSummaryAsync(new TenantId(tenantId), effectiveFrom, effectiveUntil, ct);
        return Results.Ok(summaries.Select(MapSummaryToDto).ToList());
    }

    private static async Task<IResult> GetUsageDetails(
        string tenantId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? until,
        [FromQuery] string? type,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] IUsageRecordStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        var effectiveFrom = from ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var effectiveUntil = until ?? now;

        UsageType? typeFilter = null;
        if (!string.IsNullOrEmpty(type))
        {
            if (!Enum.TryParse<UsageType>(type, out var parsed))
                return Results.BadRequest(new ErrorResponse($"Unknown usage type: {type}"));
            typeFilter = parsed;
        }

        var records = await store.ListAsync(new TenantId(tenantId), effectiveFrom, effectiveUntil, typeFilter, page ?? 1, pageSize ?? 50, ct);
        return Results.Ok(records.Select(MapRecordToDto).ToList());
    }

    // ─── Quota Handlers ──────────────────────────────────────────────────────────

    private static async Task<IResult> GetQuotaStatus(
        string tenantId,
        [FromServices] IQuotaEnforcementService service,
        CancellationToken ct)
    {
        var status = await service.GetQuotaStatusAsync(new TenantId(tenantId), ct);

        return Results.Ok(new QuotaStatusDto(
            status.TenantId.Value,
            status.Quota is not null ? MapQuotaToDto(status.Quota) : null,
            status.CurrentUsage.Select(MapSummaryToDto).ToList()));
    }

    private static async Task<IResult> UpdateQuota(
        string tenantId,
        [FromBody] UpdateQuotaRequest body,
        [FromServices] ITenantQuotaStore store,
        CancellationToken ct)
    {
        if (body.QuotaAction is not null && !Enum.TryParse<QuotaAction>(body.QuotaAction, out _))
            return Results.BadRequest(new ErrorResponse($"Unknown quota action: {body.QuotaAction}"));

        var tid = new TenantId(tenantId);
        var existing = await store.GetAsync(tid, ct);

        var quota = new TenantQuota
        {
            TenantId = tid,
            MaxConcurrentChannels = body.MaxConcurrentChannels ?? existing?.MaxConcurrentChannels ?? 100,
            MaxActiveCampaigns = body.MaxActiveCampaigns ?? existing?.MaxActiveCampaigns ?? 10,
            MaxMonthlyVoiceMinutes = body.MaxMonthlyVoiceMinutes ?? existing?.MaxMonthlyVoiceMinutes,
            MaxMonthlyMessages = body.MaxMonthlyMessages ?? existing?.MaxMonthlyMessages,
            MaxStorageBytes = body.MaxStorageBytes ?? existing?.MaxStorageBytes,
            MaxActiveAgents = body.MaxActiveAgents ?? existing?.MaxActiveAgents,
            QuotaAction = body.QuotaAction is not null
                ? Enum.Parse<QuotaAction>(body.QuotaAction)
                : existing?.QuotaAction ?? QuotaAction.Warn,
        };

        await store.UpsertAsync(quota, ct);
        return Results.Ok(MapQuotaToDto(quota));
    }

    // ─── Dunning Handlers ────────────────────────────────────────────────────────

    private static async Task<IResult> GetDunning(
        string id,
        [FromServices] IDunningStore dunningStore,
        CancellationToken ct)
    {
        var record = await dunningStore.GetActiveAsync(id, ct);
        return record is null
            ? Results.NotFound()
            : Results.Ok(new DunningRecordDto(
                record.DunningId,
                record.TenantId,
                record.InvoiceId,
                record.CurrentStage.ToString(),
                record.StartedAt,
                record.EscalatedAt,
                record.ResolvedAt,
                record.IsPaused,
                record.IsActive));
    }

    private static async Task<IResult> PauseDunning(
        string id,
        [FromServices] IDunningStore dunningStore,
        CancellationToken ct)
    {
        var record = await dunningStore.GetActiveAsync(id, ct);
        if (record is null)
            return Results.NotFound();

        record.IsPaused = !record.IsPaused;
        await dunningStore.UpsertAsync(record, ct);
        return Results.Ok(new DunningRecordDto(
            record.DunningId,
            record.TenantId,
            record.InvoiceId,
            record.CurrentStage.ToString(),
            record.StartedAt,
            record.EscalatedAt,
            record.ResolvedAt,
            record.IsPaused,
            record.IsActive));
    }

    /// <summary>
    /// R5.3 Phase A — Task A.7. Explicit mirror of <see cref="PauseDunning"/>
    /// that always clears the paused flag (vs the legacy toggle semantics) and
    /// emits a <c>billing.dunning.resumed</c> audit entry. Frontend S4.2
    /// (Phase B B.2) uses this dedicated action to restore dunning processing
    /// after a hold period.
    /// </summary>
    private static async Task<IResult> ResumeDunning(
        string id,
        HttpContext context,
        [FromServices] IDunningStore dunningStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var record = await dunningStore.GetActiveAsync(id, ct);
        if (record is null)
            return Results.NotFound();

        var wasPaused = record.IsPaused;
        record.IsPaused = false;
        await dunningStore.UpsertAsync(record, ct);

        var actor = context.User.FindFirst("sub")?.Value
                    ?? context.User.FindFirst("user_id")?.Value
                    ?? "system";

        await audit.RecordAsync(
            new TenantId(id),
            category: "billing",
            action: "billing.dunning.resumed",
            severity: "info",
            actorId: actor,
            actorType: "user",
            targetId: id,
            targetType: "tenant",
            changes: new AuditChanges(
                Before: new { IsPaused = wasPaused },
                After: new { record.IsPaused }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
                ["dunningId"] = record.DunningId,
            },
            ct: ct);

        return Results.Ok(new DunningRecordDto(
            record.DunningId,
            record.TenantId,
            record.InvoiceId,
            record.CurrentStage.ToString(),
            record.StartedAt,
            record.EscalatedAt,
            record.ResolvedAt,
            record.IsPaused,
            record.IsActive));
    }

    private static async Task<IResult> PayInvoice(
        string invoiceId,
        [FromServices] IInvoiceStore invoiceStore,
        [FromServices] IDunningStore dunningStore,
        [FromServices] ITenantStore tenantStore,
        [FromServices] TenantTierCache tierCache,
        [FromServices] FeatureGateCache featureGateCache,
        CancellationToken ct)
    {
        var dunningRecord = await dunningStore.GetByInvoiceAsync(invoiceId, ct);
        if (dunningRecord is null)
        {
            // Try to find the invoice without a known tenantId — search by dunning first
            return Results.NotFound();
        }

        var invoice = await invoiceStore.GetByIdAsync(
            new TenantId(dunningRecord.TenantId),
            EntityId.From(invoiceId),
            ct);

        if (invoice is null)
            return Results.NotFound();

        invoice.Status = InvoiceStatus.Paid;
        invoice.PaymentStatus = PaymentStatus.Current;
        invoice.PaidAt = DateTimeOffset.UtcNow;
        await invoiceStore.SaveAsync(invoice, ct);

        dunningRecord.IsActive = false;
        dunningRecord.ResolvedAt = DateTimeOffset.UtcNow;
        await dunningStore.UpsertAsync(dunningRecord, ct);

        await tenantStore.UpdateStatusAsync(dunningRecord.TenantId, TenantStatus.Active, ct);
        tierCache.Remove(dunningRecord.TenantId);
        featureGateCache.Remove(dunningRecord.TenantId);

        return Results.Ok(new MessageResponse("Invoice marked as paid"));
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

    private static UsageRecordDto MapRecordToDto(UsageRecord r) => new(
        r.RecordId.Value, r.UsageType.ToString(), r.Quantity, r.Unit.ToString(),
        r.Channel, r.ReferenceId, r.RecordedAt);

    private static QuotaDto MapQuotaToDto(TenantQuota q) => new(
        q.MaxConcurrentChannels, q.MaxActiveCampaigns,
        q.MaxMonthlyVoiceMinutes, q.MaxMonthlyMessages,
        q.MaxStorageBytes, q.MaxActiveAgents, q.QuotaAction.ToString());
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

// Rate Cards
internal sealed record RateCardDto(
    string RateCardId, string TenantId, string Name, string Currency,
    DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo,
    bool IsDefault, IReadOnlyList<RateEntryDto> Rates);

internal sealed record RateEntryDto(
    string UsageType, decimal UnitPrice, decimal IncludedQuantity,
    IReadOnlyList<RateTierDto>? Tiers);

internal sealed record RateTierDto(decimal FromQuantity, decimal? ToQuantity, decimal UnitPrice);

internal sealed record CreateRateCardRequest(
    string Name, string Currency, DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo, bool IsDefault, IReadOnlyList<RateEntryDto> Rates);

// Invoices
internal sealed record InvoiceDto(
    string InvoiceId, string TenantId, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    string Currency, IReadOnlyList<InvoiceLineItemDto> LineItems,
    decimal Subtotal, decimal Tax, decimal Total,
    string Status, DateTimeOffset GeneratedAt, DateTimeOffset? IssuedAt, DateTimeOffset? PaidAt,
    string PaymentStatus, DateTimeOffset? DueDate);

internal sealed record InvoiceLineItemDto(
    string UsageType, string Description, decimal Quantity, decimal UnitPrice,
    decimal Amount, decimal IncludedQuantity, decimal OverageQuantity);

internal sealed record GenerateInvoiceRequest(DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd);

// Usage
internal sealed record UsageSummaryDto(
    string UsageType, decimal TotalQuantity, int RecordCount,
    DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, DateTimeOffset LastUpdatedAt);

internal sealed record UsageRecordDto(
    string RecordId, string UsageType, decimal Quantity, string Unit,
    string? Channel, string? ReferenceId, DateTimeOffset RecordedAt);

// Quotas
internal sealed record QuotaStatusDto(
    string TenantId, QuotaDto? Quota, IReadOnlyList<UsageSummaryDto> CurrentUsage);

internal sealed record QuotaDto(
    int MaxConcurrentChannels, int MaxActiveCampaigns,
    long? MaxMonthlyVoiceMinutes, long? MaxMonthlyMessages,
    long? MaxStorageBytes, int? MaxActiveAgents, string QuotaAction);

internal sealed record UpdateQuotaRequest(
    int? MaxConcurrentChannels = null, int? MaxActiveCampaigns = null,
    long? MaxMonthlyVoiceMinutes = null, long? MaxMonthlyMessages = null,
    long? MaxStorageBytes = null, int? MaxActiveAgents = null, string? QuotaAction = null);

// Dunning
internal sealed record DunningRecordDto(
    string DunningId,
    string TenantId,
    string InvoiceId,
    string CurrentStage,
    DateTimeOffset StartedAt,
    DateTimeOffset? EscalatedAt,
    DateTimeOffset? ResolvedAt,
    bool IsPaused,
    bool IsActive);
