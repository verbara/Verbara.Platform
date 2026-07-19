using System.Security.Claims;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// C2 (P2c.2) — read-only tenant view of platform-managed AI credit usage.
/// <para>
/// <c>GET /admin/ai/credits</c> reads the tenant quota (<c>AiCreditsMonthly</c> allowance) and the
/// current calendar-month <see cref="UsageType.AiAnalysis"/> token summary, converting raw tokens to
/// commercial AI Credits via <see cref="PlatformLlmOptions.CreditTokenRatio"/> (aggregate, never
/// per-call). <c>AdminOnly</c> + operational-tenant gated, and additionally requires the
/// <c>typification:ai:configure</c> permission (the same audience that edits a tenant's AI config).
/// </para>
/// </summary>
internal static class AiCreditsEndpoints
{
    public static void MapAiCreditsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/admin/ai/credits")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant()
            .MapGet("", GetCredits);
    }

    private static async Task<Results<Ok<AiCreditsResponse>, ForbidHttpResult>> GetCredits(
        HttpContext context,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] IUsageRecordStore usageStore,
        [FromServices] PermissionResolver permissionResolver,
        [FromServices] IOptions<PlatformLlmOptions> platform,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var perms = await ResolveCallerPermissions(context, permissionResolver, tenantId, ct);
        if (!PermissionResolver.HasPermission(perms, "typification:ai:configure"))
            return TypedResults.Forbid();

        var ratio = Math.Max(1, platform.Value.CreditTokenRatio);
        var (start, end, _) = BillingPeriod.Current(clock);

        var summary = await usageStore.GetSummaryByTypeAsync(tenantId, UsageType.AiAnalysis, start, end, ct);
        var consumedTokens = summary?.TotalQuantity ?? 0m;
        var consumedCredits = (long)(consumedTokens / ratio);

        var quota = await quotaStore.GetAsync(tenantId, ct);
        long? allowance = quota?.AiCreditsMonthly;
        long? remaining = allowance is { } a ? Math.Max(0, a - consumedCredits) : null;
        double percent = allowance is { } a2 && a2 > 0 ? (double)consumedCredits / a2 * 100 : 0;

        return TypedResults.Ok(new AiCreditsResponse(
            AllowanceCredits: allowance,
            ConsumedCredits: consumedCredits,
            RemainingCredits: remaining,
            UsagePercent: percent,
            PeriodEnd: end,
            ActionOnExhaustion: (quota?.QuotaAction ?? QuotaAction.Warn).ToString()));
    }

    // ─── helpers (mirror the TypificationEndpoints canonical-order resolution) ────

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }

    private static async Task<IReadOnlySet<string>> ResolveCallerPermissions(
        HttpContext context,
        PermissionResolver permissionResolver,
        TenantId tenantId,
        CancellationToken ct)
    {
        var callerUserId = context.User.FindFirstValue("user_id")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");

        if (string.IsNullOrEmpty(callerUserId))
            return new HashSet<string>(StringComparer.Ordinal);

        return await permissionResolver.ResolveAsync(tenantId, EntityId.From(callerUserId), ct);
    }
}

/// <summary>
/// C2 (P2c.2) — tenant AI credit usage readout. Credits are derived by aggregation (Σtokens ÷
/// <c>PlatformLlmOptions.CreditTokenRatio</c>); the operator key/model are NEVER exposed here.
/// </summary>
/// <param name="AllowanceCredits">Monthly AI Credit allowance, or <see langword="null"/> for unlimited / pay-as-you-go.</param>
/// <param name="ConsumedCredits">Credits consumed in the current calendar month (floor of tokens ÷ ratio).</param>
/// <param name="RemainingCredits">Allowance minus consumed (floored at 0), or <see langword="null"/> when unlimited.</param>
/// <param name="UsagePercent">Consumed ÷ allowance × 100, or 0 when the allowance is unlimited / zero.</param>
/// <param name="PeriodEnd">Exclusive end of the current usage period (first instant of next month, UTC).</param>
/// <param name="ActionOnExhaustion">The tenant's <c>QuotaAction</c> name (Warn / SoftBlock / HardBlock).</param>
internal sealed record AiCreditsResponse(
    long? AllowanceCredits,
    long ConsumedCredits,
    long? RemainingCredits,
    double UsagePercent,
    DateTimeOffset PeriodEnd,
    string ActionOnExhaustion);
