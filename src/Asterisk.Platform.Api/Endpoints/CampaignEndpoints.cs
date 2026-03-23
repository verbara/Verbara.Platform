using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.Dialer.Campaign;
using Asterisk.Sdk.Pro.Dialer.Models;

namespace Asterisk.Platform.Api.Endpoints;

internal static class CampaignEndpoints
{
    public static void MapCampaignEndpoints(this IEndpointRouteBuilder app)
    {
        var campaigns = app.MapGroup("/api/admin/campaigns").RequireAuthorization();

        // CRUD
        campaigns.MapPost("/", CreateCampaign);
        campaigns.MapGet("/", ListCampaigns);
        campaigns.MapGet("/{id:long}", GetCampaign);
        campaigns.MapPut("/{id:long}", UpdateCampaign);
        campaigns.MapDelete("/{id:long}", DeleteCampaign);

        // Lifecycle
        campaigns.MapPost("/{id:long}/start", StartCampaign);
        campaigns.MapPost("/{id:long}/pause", PauseCampaign);
        campaigns.MapPost("/{id:long}/resume", ResumeCampaign);
        campaigns.MapPost("/{id:long}/stop", StopCampaign);
    }

    // ─── CRUD Handlers ────────────────────────────────────────────────────────

    private static async Task<IResult> CreateCampaign(
        HttpContext context,
        CreateCampaignRequest body,
        CampaignStoreBase campaignStore,
        PlatformEventBus eventBus,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var campaign = MapFromRequest(body, tenantId);
        var id = await campaignStore.CreateCampaignAsync(tenantId, campaign, ct);
        campaign.Id = id;
        eventBus.Publish(new CampaignStatusChangedEvent(
            tenantId, id, campaign.Name, "", campaign.Status.ToString()));
        return Results.Created($"/api/admin/campaigns/{id}", MapToSummary(campaign));
    }

    private static async Task<IResult> ListCampaigns(
        HttpContext context,
        CampaignStoreBase campaignStore,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var items = await campaignStore.ListCampaignsAsync(tenantId, page, pageSize, ct);
        var total = await campaignStore.CountCampaignsAsync(tenantId, ct);
        var dtos = items.Select(MapToSummary).ToList();
        return Results.Ok(new PagedResult<CampaignSummaryDto>(dtos, total, page, pageSize));
    }

    private static async Task<IResult> GetCampaign(
        long id,
        HttpContext context,
        CampaignStoreBase campaignStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var campaign = await campaignStore.GetCampaignAsync(tenantId, id, ct);
        return campaign is null ? Results.NotFound() : Results.Ok(MapToDetail(campaign));
    }

    private static async Task<IResult> UpdateCampaign(
        long id,
        HttpContext context,
        UpdateCampaignRequest body,
        CampaignStoreBase campaignStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var campaign = await campaignStore.GetCampaignAsync(tenantId, id, ct);
        if (campaign is null)
            return Results.NotFound();

        if (body.Name is not null) campaign.Name = body.Name;
        if (body.Description is not null) campaign.Description = body.Description;
        if (body.TargetQueueName is not null) campaign.TargetQueueName = body.TargetQueueName;
        if (body.MaxConcurrentCalls.HasValue) campaign.MaxConcurrentCalls = body.MaxConcurrentCalls.Value;
        if (body.PowerRatio.HasValue) campaign.PowerRatio = body.PowerRatio.Value;
        if (body.TargetAbandonRate.HasValue) campaign.TargetAbandonRate = body.TargetAbandonRate.Value;
        if (body.Timezone is not null) campaign.ContactTimezone = body.Timezone;
        if (body.CampaignStart is not null) campaign.StartsAt = DateTimeOffset.Parse(body.CampaignStart);
        if (body.CampaignEnd is not null) campaign.EndsAt = DateTimeOffset.Parse(body.CampaignEnd);
        if (body.DncEnabled.HasValue) campaign.CheckGlobalDnc = body.DncEnabled.Value;
        if (body.MaxAttemptsPerContact.HasValue) campaign.MaxAttemptsPerContact = body.MaxAttemptsPerContact.Value;
        if (body.RetryIntervalMinutes.HasValue) campaign.DefaultRetryDelayMinutes = body.RetryIntervalMinutes.Value;
        if (body.TimeBetweenAttemptsMinutes.HasValue) campaign.ImmediateRetryDelayMs = body.TimeBetweenAttemptsMinutes.Value * 60000;
        if (body.ComplianceNotes is not null)
        {
            campaign.Metadata ??= new Dictionary<string, string>();
            campaign.Metadata["compliance_notes"] = body.ComplianceNotes;
        }
        if (body.Schedule is not null)
            campaign.ScheduleDays = body.Schedule.Select(MapScheduleDay).ToList();
        if (body.Holidays is not null)
        {
            campaign.Metadata ??= new Dictionary<string, string>();
            campaign.Metadata["holidays"] = string.Join(",", body.Holidays);
        }

        await campaignStore.UpdateCampaignAsync(tenantId, campaign, ct);
        return Results.Ok(MapToDetail(campaign));
    }

    private static async Task<IResult> DeleteCampaign(
        long id,
        HttpContext context,
        CampaignStoreBase campaignStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await campaignStore.DeleteCampaignAsync(tenantId, id, ct);
        return Results.NoContent();
    }

    // ─── Lifecycle Handlers ───────────────────────────────────────────────────

    private static async Task<IResult> StartCampaign(
        long id,
        HttpContext context,
        CampaignStoreBase campaignStore,
        CampaignLifecycleManager lifecycleManager,
        PlatformEventBus eventBus,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var campaign = await campaignStore.GetCampaignAsync(tenantId, id, ct);
        if (campaign is null) return Results.NotFound();
        var oldStatus = campaign.Status.ToString();
        var success = await lifecycleManager.StartCampaignAsync(tenantId, id, ct);
        if (!success) return Results.BadRequest("Invalid status transition");
        eventBus.Publish(new CampaignStatusChangedEvent(
            tenantId, id, campaign.Name, oldStatus, CampaignStatus.Active.ToString()));
        return Results.Ok();
    }

    private static async Task<IResult> PauseCampaign(
        long id,
        HttpContext context,
        CampaignStoreBase campaignStore,
        CampaignLifecycleManager lifecycleManager,
        PlatformEventBus eventBus,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var campaign = await campaignStore.GetCampaignAsync(tenantId, id, ct);
        if (campaign is null) return Results.NotFound();
        var oldStatus = campaign.Status.ToString();
        var success = await lifecycleManager.PauseCampaignAsync(tenantId, id, ct);
        if (!success) return Results.BadRequest("Invalid status transition");
        eventBus.Publish(new CampaignStatusChangedEvent(
            tenantId, id, campaign.Name, oldStatus, CampaignStatus.Paused.ToString()));
        return Results.Ok();
    }

    private static async Task<IResult> ResumeCampaign(
        long id,
        HttpContext context,
        CampaignStoreBase campaignStore,
        CampaignLifecycleManager lifecycleManager,
        PlatformEventBus eventBus,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var campaign = await campaignStore.GetCampaignAsync(tenantId, id, ct);
        if (campaign is null) return Results.NotFound();
        var oldStatus = campaign.Status.ToString();
        var success = await lifecycleManager.StartCampaignAsync(tenantId, id, ct);
        if (!success) return Results.BadRequest("Invalid status transition");
        eventBus.Publish(new CampaignStatusChangedEvent(
            tenantId, id, campaign.Name, oldStatus, CampaignStatus.Active.ToString()));
        return Results.Ok();
    }

    private static async Task<IResult> StopCampaign(
        long id,
        HttpContext context,
        CampaignStoreBase campaignStore,
        CampaignLifecycleManager lifecycleManager,
        PlatformEventBus eventBus,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var campaign = await campaignStore.GetCampaignAsync(tenantId, id, ct);
        if (campaign is null) return Results.NotFound();
        var oldStatus = campaign.Status.ToString();
        var success = await lifecycleManager.CompleteCampaignAsync(tenantId, id, ct);
        if (!success) return Results.BadRequest("Invalid status transition");
        eventBus.Publish(new CampaignStatusChangedEvent(
            tenantId, id, campaign.Name, oldStatus, CampaignStatus.Completed.ToString()));
        return Results.Ok();
    }

    // ─── Mapping Helpers ──────────────────────────────────────────────────────

    private static CampaignSummaryDto MapToSummary(Campaign c) =>
        new(
            Id: c.Id,
            Name: c.Name,
            Status: c.Status.ToString(),
            QueueName: c.TargetQueueName ?? "",
            Mode: c.Mode.ToString(),
            TotalContacts: 0,
            ContactsDialed: 0);

    private static CampaignDetailDto MapToDetail(Campaign c) =>
        new(
            Id: c.Id,
            Name: c.Name,
            Description: c.Description,
            Status: c.Status.ToString(),
            Mode: c.Mode.ToString(),
            QueueName: c.TargetQueueName ?? "",
            TeamName: null,
            MaxConcurrentCalls: c.MaxConcurrentCalls,
            PowerRatio: c.PowerRatio,
            TargetAbandonRate: c.TargetAbandonRate,
            Timezone: c.ContactTimezone ?? "UTC",
            CampaignStart: c.StartsAt?.ToString("O"),
            CampaignEnd: c.EndsAt?.ToString("O"),
            Schedule: c.ScheduleDays.Select(d => new ScheduleDayDto(
                Day: d.DayOfWeek.ToString(),
                Enabled: d.Enabled,
                Start: d.StartTime.ToString("HH:mm"),
                End: d.EndTime.ToString("HH:mm"))).ToArray(),
            Holidays: c.Metadata is not null && c.Metadata.TryGetValue("holidays", out var h)
                ? h.Split(',', StringSplitOptions.RemoveEmptyEntries)
                : [],
            DncEnabled: c.CheckGlobalDnc,
            MaxAttemptsPerContact: c.MaxAttemptsPerContact,
            RetryIntervalMinutes: c.DefaultRetryDelayMinutes ?? 0,
            TimeBetweenAttemptsMinutes: c.ImmediateRetryDelayMs / 60000,
            ComplianceNotes: c.Metadata is not null && c.Metadata.TryGetValue("compliance_notes", out var cn) ? cn : null,
            TotalContacts: 0,
            ContactsDialed: 0,
            CreatedAt: c.CreatedAt);

    private static Campaign MapFromRequest(CreateCampaignRequest req, string tenantId)
    {
        var mode = req.Mode.ToLowerInvariant() switch
        {
            "preview" => DialingMode.Preview,
            "progressive" => DialingMode.Progressive,
            "power" => DialingMode.Power,
            "predictive" => DialingMode.Predictive,
            "agentless" or "robot" => DialingMode.Robot,
            _ => DialingMode.Progressive,
        };

        var metadata = new Dictionary<string, string>();
        if (req.ComplianceNotes is not null) metadata["compliance_notes"] = req.ComplianceNotes;
        if (req.Holidays.Length > 0) metadata["holidays"] = string.Join(",", req.Holidays);

        return new Campaign
        {
            TenantId = tenantId,
            Name = req.Name,
            Description = req.Description,
            Status = CampaignStatus.Draft,
            Mode = mode,
            TargetQueueName = req.TargetQueueName,
            MaxConcurrentCalls = req.MaxConcurrentCalls,
            PowerRatio = req.PowerRatio ?? 1.0,
            TargetAbandonRate = req.TargetAbandonRate ?? 0.03,
            ContactTimezone = req.Timezone,
            StartsAt = req.CampaignStart is not null ? DateTimeOffset.Parse(req.CampaignStart) : null,
            EndsAt = req.CampaignEnd is not null ? DateTimeOffset.Parse(req.CampaignEnd) : null,
            CheckGlobalDnc = req.DncEnabled,
            MaxAttemptsPerContact = req.MaxAttemptsPerContact,
            DefaultRetryDelayMinutes = req.RetryIntervalMinutes,
            ImmediateRetryDelayMs = req.TimeBetweenAttemptsMinutes * 60000,
            AmdEnabled = false,
            CallerIdStrategy = CallerIdStrategy.Fixed,
            BlendEnabled = false,
            OverdialFactor = 0,
            DynamicChannelAllocation = false,
            DispositionsEnabled = true,
            RingTimeoutSeconds = 20,
            WrapUpTimeSeconds = 30,
            CreatedAt = DateTimeOffset.UtcNow,
            ScheduleDays = req.Schedule.Select(MapScheduleDay).ToList(),
            Metadata = metadata.Count > 0 ? metadata : null,
        };
    }

    private static CampaignScheduleDay MapScheduleDay(ScheduleDayDto dto) =>
        new()
        {
            DayOfWeek = Enum.TryParse<DayOfWeek>(dto.Day, ignoreCase: true, out var dow) ? dow : DayOfWeek.Monday,
            Enabled = dto.Enabled,
            StartTime = TimeOnly.TryParse(dto.Start, out var st) ? st : TimeOnly.MinValue,
            EndTime = TimeOnly.TryParse(dto.End, out var et) ? et : TimeOnly.MaxValue,
        };

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request/Response DTOs ─────────────────────────────────────────────────────

internal sealed record CreateCampaignRequest(
    string Name, string? Description, string Mode,
    string TargetQueueName, string? TeamId,
    int MaxConcurrentCalls, double? PowerRatio, double? TargetAbandonRate,
    string Timezone, string? CampaignStart, string? CampaignEnd,
    ScheduleDayDto[] Schedule, string[] Holidays,
    bool DncEnabled, int MaxAttemptsPerContact,
    int RetryIntervalMinutes, int TimeBetweenAttemptsMinutes,
    string? ComplianceNotes);

internal sealed record UpdateCampaignRequest(
    string? Name, string? Description, string? TargetQueueName, string? TeamId,
    int? MaxConcurrentCalls, double? PowerRatio, double? TargetAbandonRate,
    string? Timezone, string? CampaignStart, string? CampaignEnd,
    ScheduleDayDto[]? Schedule, string[]? Holidays,
    bool? DncEnabled, int? MaxAttemptsPerContact,
    int? RetryIntervalMinutes, int? TimeBetweenAttemptsMinutes,
    string? ComplianceNotes);

internal sealed record ScheduleDayDto(string Day, bool Enabled, string Start, string End);

internal sealed record CampaignSummaryDto(
    long Id, string Name, string Status, string QueueName, string Mode,
    int TotalContacts, int ContactsDialed);

internal sealed record CampaignDetailDto(
    long Id, string Name, string? Description, string Status, string Mode,
    string QueueName, string? TeamName,
    int MaxConcurrentCalls, double? PowerRatio, double? TargetAbandonRate,
    string Timezone, string? CampaignStart, string? CampaignEnd,
    ScheduleDayDto[] Schedule, string[] Holidays,
    bool DncEnabled, int MaxAttemptsPerContact,
    int RetryIntervalMinutes, int TimeBetweenAttemptsMinutes,
    string? ComplianceNotes,
    int TotalContacts, int ContactsDialed, DateTimeOffset CreatedAt);
