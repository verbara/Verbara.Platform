using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class PartnerRevenueEndpoints
{
    public static RouteGroupBuilder MapPartnerRevenueEndpoints(this RouteGroupBuilder app)
    {
        var group = app.MapGroup("/partner/revenue")
            .WithTags("Partner - Revenue")
            .RequireAuthorization("PartnerAdminOnly")
            .RequireAuthorization("partner:billing:view");

        group.MapGet("/", GetRevenueSummary);
        group.MapGet("/details", GetRevenueDetails);

        return app;
    }

    // ── Summary ───────────────────────────────────────────────────────────────

    private static async Task<IResult> GetRevenueSummary(
        HttpContext context,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? until,
        [FromServices] IPartnerRevenueStore revenueStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var records = await revenueStore.ListAsync(new TenantId(callerTenantId), from, until, ct);

        var summary = new PartnerRevenueSummaryDto(
            TotalGross: records.Sum(r => r.GrossAmount),
            TotalPlatformCost: records.Sum(r => r.PlatformCost),
            TotalMargin: records.Sum(r => r.PartnerMargin),
            CustomerCount: records.Select(r => r.CustomerTenantId.Value).Distinct().Count(),
            InvoiceCount: records.Select(r => r.InvoiceId.Value).Distinct().Count());

        return Results.Ok(summary);
    }

    // ── Details ───────────────────────────────────────────────────────────────

    private static async Task<IResult> GetRevenueDetails(
        HttpContext context,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? until,
        [FromServices] IPartnerRevenueStore revenueStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var records = await revenueStore.ListAsync(new TenantId(callerTenantId), from, until, ct);

        var details = records.Select(r => new PartnerRevenueDetailDto(
            RevenueId: r.RevenueId.Value,
            CustomerTenantId: r.CustomerTenantId.Value,
            InvoiceId: r.InvoiceId.Value,
            GrossAmount: r.GrossAmount,
            PlatformCost: r.PlatformCost,
            PartnerMargin: r.PartnerMargin,
            PeriodStart: r.PeriodStart,
            PeriodEnd: r.PeriodEnd)).ToList();

        return Results.Ok(details);
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record PartnerRevenueSummaryDto(
    decimal TotalGross,
    decimal TotalPlatformCost,
    decimal TotalMargin,
    int CustomerCount,
    int InvoiceCount);

internal sealed record PartnerRevenueDetailDto(
    string RevenueId,
    string CustomerTenantId,
    string InvoiceId,
    decimal GrossAmount,
    decimal PlatformCost,
    decimal PartnerMargin,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd);
