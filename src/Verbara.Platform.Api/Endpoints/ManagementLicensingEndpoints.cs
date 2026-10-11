using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Queues.Licensing;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// licensed-agent-metering slice 3 (licensed-agent-reporting, design D10/D11): the deployment's licensed-agent
/// peaks and the verifiable export of the ledger, for Platform administrators only. Mapped from inside
/// <see cref="ManagementSystemEndpoints.MapManagementSystemEndpoints"/>, so Program.cs does not grow. Both are
/// read-only: no ledger, daily or audit row is ever written by them.
/// </summary>
internal static class ManagementLicensingEndpoints
{
    public static void MapManagementLicensingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management/licensing").RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/agents", GetPeaks);
        group.MapGet("/agents/export", GetExport);
    }

    private static async Task<Results<Ok<LicensedAgentPeaksResponse>, BadRequest<ErrorResponse>>> GetPeaks(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromServices] ILicensedAgentReportReader reader,
        [FromServices] ILicenseStatus licenseStatus,
        [FromServices] IConfiguration configuration,
        CancellationToken ct)
    {
        if (LicensedAgentReports.ParseRange(from, to) is not { } range)
            return TypedResults.BadRequest(new ErrorResponse(LicensedAgentReports.RangeError));

        var data = await reader.ReadPeaksAsync(range.From, range.To, ConfiguredDayZone(configuration), ct);
        return TypedResults.Ok(LicensedAgentReports.BuildPeaks(
            range.From, range.To, data, LicensedAgentReports.License(licenseStatus)));
    }

    private static async Task<Results<Ok<LicensedAgentExportResponse>, BadRequest<ErrorResponse>>> GetExport(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromServices] ILicensedAgentReportReader reader,
        [FromServices] ILicenseStatus licenseStatus,
        [FromServices] IConfiguration configuration,
        [FromServices] TimeProvider timeProvider,
        CancellationToken ct)
    {
        if (LicensedAgentReports.ParseRange(from, to) is not { } range)
            return TypedResults.BadRequest(new ErrorResponse(LicensedAgentReports.RangeError));

        var data = await reader.ReadExportAsync(range.From, range.To, ConfiguredDayZone(configuration), ct);
        return TypedResults.Ok(LicensedAgentReports.BuildExport(
            range.From, range.To, data, LicensedAgentReports.License(licenseStatus), timeProvider.GetUtcNow()));
    }

    // The zone the daily close is configured with; the reports use it only while the deployment chain is not
    // anchored yet (once anchored, the recorded zone wins and can never differ from a running close's).
    private static string ConfiguredDayZone(IConfiguration configuration) =>
        configuration[LicenseAgentDayZone.ConfigurationKey] is { Length: > 0 } zone ? zone : LicenseAgentDayZone.DefaultZone;
}

/// <summary>
/// The pure mapping of both reports (licensed-agent-reporting): range validation, the peaks over the latest
/// revision of each day, the server-side <c>overBand</c>, and the export rows rendered so an independent tool
/// can recompute every <c>rowHash</c> from the JSON alone.
/// </summary>
internal static class LicensedAgentReports
{
    public const int SchemaVersion = 1;

    public static readonly string RangeError =
        $"from and to are required ISO-8601 dates (yyyy-MM-dd), from must not be later than to, and the range may " +
        $"span at most {LicensedAgentExportSpan.MaxRangeDays} days.";

    /// <summary>The validated range, or null: missing or malformed dates, <c>from</c> after <c>to</c>, or more than 460 days.</summary>
    public static (DateOnly From, DateOnly To)? ParseRange(string? from, string? to)
    {
        if (!TryParseDay(from, out var f) || !TryParseDay(to, out var t) || f > t)
            return null;
        var days = t.DayNumber - f.DayNumber + 1;
        return days <= LicensedAgentExportSpan.MaxRangeDays ? (f, t) : null;
    }

    /// <summary>
    /// The licence block, each field read once from <paramref name="status"/> (design D9: a torn read during a
    /// licence swap can at worst mix fields of two licences in one response). <c>maxAgents</c> is null when no
    /// licence is loaded; null or 0 means not declared.
    /// </summary>
    public static LicensedAgentLicenseDto License(ILicenseStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var licenseId = status.LicenseId is { Length: > 0 } id ? id : null;
        var licensee = status.Licensee;
        var tier = status.Tier.ToString();
        var maxAgents = status.MaxAgents;
        return new LicensedAgentLicenseDto(licenseId, licensee, tier, licenseId is null && maxAgents == 0 ? null : maxAgents, true);
    }

    /// <summary><c>maxAgents &gt; 0 AND peak &gt; maxAgents</c>: advisory only, nothing is ever blocked on it.</summary>
    public static bool IsOverBand(int? maxAgents, int peakLicensedAgents) =>
        maxAgents is > 0 && peakLicensedAgents > maxAgents;

    public static LicensedAgentPeaksResponse BuildPeaks(
        DateOnly from, DateOnly to, LicensedAgentPeaksData data, LicensedAgentLicenseDto license)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(license);

        // Each (chain, day) cell is its highest revision; a day is reported once its deployment total is closed.
        var latest = data.Daily
            .Where(d => d.Day >= from && d.Day <= to)
            .GroupBy(d => (d.TenantId, d.Day))
            .Select(g => g.MaxBy(d => d.Revision)!)
            .ToList();

        var days = latest
            .Where(d => d.TenantId is null)
            .OrderBy(d => d.Day)
            .Select(deployment => new LicensedAgentDayDto(
                deployment.Day,
                deployment.LicensedAgents,
                deployment.RowHash,
                latest
                    .Where(d => d.TenantId is not null && d.Day == deployment.Day)
                    .OrderBy(d => d.TenantId, StringComparer.Ordinal)
                    .Select(d => new LicensedAgentTenantDayDto(
                        d.TenantId!,
                        data.Tenants.TryGetValue(d.TenantId!, out var tenant) ? tenant.Name : d.TenantId!,
                        tenant?.ParentTenantId,
                        d.LicensedAgents,
                        d.Revision,
                        LicenseAgentChain.FormatInstant(d.ClosedAt),
                        d.RowHash))
                    .ToList()))
            .ToList();

        // The peak day is the earliest day that reaches the range's highest total.
        var peak = days.Count == 0 ? null : days.OrderByDescending(d => d.DeploymentLicensedAgents).ThenBy(d => d.Day).First();
        var peakAgents = peak?.DeploymentLicensedAgents ?? 0;

        return new LicensedAgentPeaksResponse(
            SchemaVersion,
            from,
            to,
            data.DayZone,
            license,
            new LicensedAgentDeploymentPeakDto(
                peakAgents, peak?.Day, IsOverBand(license.MaxAgents, peakAgents), PartnerSubtotals(peak, data.Tenants)),
            days,
            Heads(data.ChainHeads));
    }

    /// <summary>
    /// partner-channel-pilot-fixes (design D8): on the deployment's peak day only — the day that sets the resale
    /// share — one entry per tenant that is a Partner when the report is read and the parent of at least one tenant
    /// row that day, summing those rows. Never a sum of each tenant's own peak. Direct Customers get no entry.
    /// </summary>
    private static List<LicensedAgentPartnerSubtotalDto> PartnerSubtotals(
        LicensedAgentDayDto? peak, IReadOnlyDictionary<string, LicensedAgentTenantRef> tenants)
    {
        if (peak is null)
            return [];

        return peak.Tenants
            .Where(t => t.ParentTenantId is { } parent
                && tenants.TryGetValue(parent, out var p) && p.Type == LicensedAgentTenantRef.PartnerType)
            .GroupBy(t => t.ParentTenantId!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new LicensedAgentPartnerSubtotalDto(g.Key, tenants[g.Key].Name, g.Sum(t => t.LicensedAgents)))
            .ToList();
    }

    /// <summary>
    /// partner-channel-pilot-fixes (design D9): every tenant the export's rows and heads name plus every parent above
    /// them, ordered by id (ordinal), outside every hashed row. A tenant whose row no longer exists has a null type
    /// and a null parent.
    /// </summary>
    private static List<LicensedAgentExportTenantDto> TenantMap(
        IEnumerable<string?> tenantIds, IReadOnlyDictionary<string, LicensedAgentTenantRef> tenants)
    {
        var members = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(tenantIds.OfType<string>());
        while (pending.TryDequeue(out var tenantId))
        {
            if (members.Add(tenantId) && tenants.TryGetValue(tenantId, out var tenant) && tenant.ParentTenantId is { } parent)
                pending.Enqueue(parent);
        }

        return members
            .Select(id => tenants.TryGetValue(id, out var tenant)
                ? new LicensedAgentExportTenantDto(id, tenant.Type, tenant.ParentTenantId)
                : new LicensedAgentExportTenantDto(id, null, null))
            .ToList();
    }

    public static LicensedAgentExportResponse BuildExport(
        DateOnly from, DateOnly to, LicensedAgentExportData data, LicensedAgentLicenseDto license, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(license);

        var events = data.Events
            .OrderBy(e => e.TenantId, ChainOrder)
            .ThenBy(e => e.Sequence)
            .Select(e => new LicensedAgentExportEventDto(
                e.TenantId,
                e.Sequence,
                e.EventId.ToString("D", CultureInfo.InvariantCulture),
                e.Kind,
                LicenseAgentChain.FormatInstant(e.OccurredAt),
                e.AgentId,
                e.UserId,
                e.ActorUserId,
                e.ConversationId,
                e.UserStatus,
                e.Counted,
                e.LicenseId,
                e.PrevHash,
                e.RowHash))
            .ToList();

        var daily = data.Daily
            .OrderBy(d => d.TenantId, ChainOrder)
            .ThenBy(d => d.Sequence)
            .Select(d => new LicensedAgentExportDailyDto(
                d.TenantId,
                d.Sequence,
                d.Day,
                d.Revision,
                d.LicensedAgents,
                LicenseAgentChain.FormatInstant(d.ClosedAt),
                d.ClosedThroughSequence,
                d.LicenseId,
                d.PrevHash,
                d.RowHash))
            .ToList();

        return new LicensedAgentExportResponse(
            SchemaVersion,
            LicenseAgentChain.Scheme,
            LicenseAgentChain.FormatInstant(LicenseAgentChain.Normalize(generatedAt)),
            from,
            to,
            data.DayZone,
            license,
            events,
            daily,
            Heads(data.ChainHeads),
            TenantMap(
                data.Events.Select(e => e.TenantId).Concat(data.Daily.Select(d => d.TenantId)).Concat(data.ChainHeads.Select(h => h.TenantId)),
                data.Tenants));
    }

    private static readonly Comparer<string?> ChainOrder = Comparer<string?>.Create(LicensedAgentExportSpan.CompareChains);

    private static List<LicensedAgentChainHeadDto> Heads(IEnumerable<LicenseAgentChainHead> heads) =>
        heads
            .OrderBy(h => h.TenantId, ChainOrder)
            .Select(h => new LicensedAgentChainHeadDto(h.TenantId, h.HeadSequence, h.HeadHash, h.LicenseId))
            .ToList();

    private static bool TryParseDay(string? value, out DateOnly day) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
}

// ─── DTOs (fixtures/licensed-agent-peaks.v1.json and licensed-agent-export.v1.json, field for field) ────────
// Nullable fields are always written (never omitted), so a reader sees `null` exactly where the fixtures
// declare one and an independent verifier renders it as the empty string of the canonical form.

internal sealed record LicensedAgentLicenseDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LicenseId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Licensee,
    string Tier,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? MaxAgents,
    bool MaxAgentsAdvisory);

internal sealed record LicensedAgentDeploymentPeakDto(
    int PeakLicensedAgents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateOnly? PeakDay,
    bool OverBand,
    IReadOnlyList<LicensedAgentPartnerSubtotalDto> PartnerSubtotals);

/// <summary>partner-channel-pilot-fixes (design D8): one Partner's share of the deployment's peak day.</summary>
internal sealed record LicensedAgentPartnerSubtotalDto(
    string PartnerTenantId,
    string PartnerTenantName,
    int LicensedAgents);

internal sealed record LicensedAgentTenantDayDto(
    string TenantId,
    string TenantName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ParentTenantId,
    int LicensedAgents,
    int Revision,
    string ClosedAt,
    string RowHash);

internal sealed record LicensedAgentDayDto(
    DateOnly Day,
    int DeploymentLicensedAgents,
    string DeploymentRowHash,
    IReadOnlyList<LicensedAgentTenantDayDto> Tenants);

internal sealed record LicensedAgentChainHeadDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TenantId,
    long HeadSequence,
    string HeadHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LicenseId);

internal sealed record LicensedAgentPeaksResponse(
    int SchemaVersion,
    DateOnly From,
    DateOnly To,
    string DayZone,
    LicensedAgentLicenseDto License,
    LicensedAgentDeploymentPeakDto Deployment,
    IReadOnlyList<LicensedAgentDayDto> Days,
    IReadOnlyList<LicensedAgentChainHeadDto> ChainHeads);

internal sealed record LicensedAgentExportEventDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TenantId,
    long Sequence,
    string EventId,
    string Kind,
    string OccurredAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AgentId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? UserId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ActorUserId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ConversationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? UserStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? Counted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LicenseId,
    string PrevHash,
    string RowHash);

internal sealed record LicensedAgentExportDailyDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TenantId,
    long Sequence,
    DateOnly Day,
    int Revision,
    int LicensedAgents,
    string ClosedAt,
    long ClosedThroughSequence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LicenseId,
    string PrevHash,
    string RowHash);

internal sealed record LicensedAgentExportResponse(
    int SchemaVersion,
    string HashScheme,
    string GeneratedAt,
    DateOnly From,
    DateOnly To,
    string DayZone,
    LicensedAgentLicenseDto License,
    IReadOnlyList<LicensedAgentExportEventDto> Events,
    IReadOnlyList<LicensedAgentExportDailyDto> Daily,
    IReadOnlyList<LicensedAgentChainHeadDto> ChainHeads,
    IReadOnlyList<LicensedAgentExportTenantDto> Tenants);

/// <summary>
/// partner-channel-pilot-fixes (design D9): the export's tenant map, a snapshot as of <c>generatedAt</c> outside
/// every hashed row. <see cref="TenantType"/> is <c>Platform</c>, <c>Partner</c> or <c>Customer</c>, or null (as is
/// <see cref="ParentTenantId"/>) when the tenant row no longer exists.
/// </summary>
internal sealed record LicensedAgentExportTenantDto(
    string TenantId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TenantType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ParentTenantId);
