using System.Globalization;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.Dialer.Campaign;
using Asterisk.Sdk.Pro.Dialer.Contacts;
using Asterisk.Sdk.Pro.Dialer.Dispositions;
using Asterisk.Sdk.Pro.Dialer.Models;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class CampaignEndpoints
{
    public static void MapCampaignEndpoints(this IEndpointRouteBuilder app)
    {
        var campaigns = app.MapGroup("/api/admin/campaigns").RequireAuthorization("AdminOnly");

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

        // Contact Lists
        campaigns.MapGet("/{id:long}/contact-lists", ListContactLists);
        campaigns.MapPost("/{id:long}/contact-lists", CreateContactList);
        campaigns.MapDelete("/{id:long}/contact-lists/{listId:long}", DeleteContactList);
        campaigns.MapPost("/{id:long}/contact-lists/{listId:long}/import", ImportContacts);
        campaigns.MapGet("/{id:long}/contact-lists/{listId:long}/contacts", ListContacts);

        // Dispositions
        campaigns.MapGet("/{id:long}/dispositions", ListDispositions);
        campaigns.MapPost("/{id:long}/dispositions", CreateDisposition);
        campaigns.MapPut("/{id:long}/dispositions/{codeId:long}", UpdateDisposition);
        campaigns.MapDelete("/{id:long}/dispositions/{codeId:long}", DeleteDisposition);

        // Callbacks
        campaigns.MapGet("/{id:long}/callbacks", ListCallbacks);
        campaigns.MapPost("/{id:long}/callbacks", CreateCallback);

        // Metrics
        campaigns.MapGet("/{id:long}/metrics", GetCampaignMetrics);

        // Operations
        var operations = app.MapGroup("/api/operations").RequireAuthorization("SupervisorPlus");
        operations.MapGet("/campaigns/metrics", ListActiveCampaignMetrics);
    }

    // ─── CRUD Handlers ────────────────────────────────────────────────────────

    private static async Task<IResult> CreateCampaign(
        HttpContext context,
        [FromBody] CreateCampaignRequest body,
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
        [FromBody] UpdateCampaignRequest body,
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
        if (body.CampaignStart is not null) campaign.StartsAt = DateTimeOffset.Parse(body.CampaignStart, CultureInfo.InvariantCulture);
        if (body.CampaignEnd is not null) campaign.EndsAt = DateTimeOffset.Parse(body.CampaignEnd, CultureInfo.InvariantCulture);
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

    // ─── Contact List Handlers ────────────────────────────────────────────────

    private static async Task<IResult> ListContactLists(
        long id,
        HttpContext context,
        ContactListStoreBase contactListStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var lists = await contactListStore.ListContactListsAsync(tenantId, id, ct);
        return Results.Ok(lists.Select(MapToContactListDto).ToList());
    }

    private static async Task<IResult> CreateContactList(
        long id,
        HttpContext context,
        [FromBody] CreateContactListRequest body,
        ContactListStoreBase contactListStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var list = new ContactList
        {
            TenantId = tenantId,
            CampaignId = id,
            Name = body.Name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var created = await contactListStore.CreateContactListAsync(tenantId, id, list, ct);
        return Results.Created($"/api/admin/campaigns/{id}/contact-lists/{created.Id}", MapToContactListDto(created));
    }

    private static async Task<IResult> DeleteContactList(
        long id,
        long listId,
        HttpContext context,
        ContactListStoreBase contactListStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await contactListStore.DeleteContactListAsync(tenantId, listId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ImportContacts(
        long id,
        long listId,
        HttpContext context,
        [FromBody] ImportContactsRequest body,
        ContactListStoreBase contactListStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var rows = body.Contacts.Select(c => new ContactImportRow(
            c.FirstName, c.LastName, c.Phone, c.PhoneType, c.Metadata)).ToList();
        var imported = await contactListStore.ImportContactsAsync(tenantId, listId, rows, ct);
        return Results.Ok(new ImportResultDto(imported, 0, 0));
    }

    private static async Task<IResult> ListContacts(
        long id,
        long listId,
        HttpContext context,
        ContactListStoreBase contactListStore,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var (items, total) = await contactListStore.ListContactsAsync(tenantId, listId, page, pageSize, ct);
        return Results.Ok(new PagedResult<Contact>(items, total, page, pageSize));
    }

    // ─── Disposition Handlers ─────────────────────────────────────────────────

    private static async Task<IResult> ListDispositions(
        long id,
        HttpContext context,
        DispositionCodeStoreBase dispositionStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var codes = await dispositionStore.ListByCampaignAsync(tenantId, id, ct);
        return Results.Ok(codes.Select(MapToDispositionDto).ToList());
    }

    private static async Task<IResult> CreateDisposition(
        long id,
        HttpContext context,
        [FromBody] CreateDispositionCodeRequest body,
        DispositionCodeStoreBase dispositionStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var code = new DispositionCode
        {
            TenantId = tenantId,
            CampaignId = id,
            Code = body.Code,
            Label = body.Label,
            Category = ParseDispositionCategory(body.Category),
            IsSuccess = body.IsSuccess,
            TriggerRetry = body.TriggerRetry,
            RetryDelayMinutes = body.RetryDelayMinutes,
            TriggerCallback = body.TriggerCallback,
            IsActive = true,
        };
        var created = await dispositionStore.CreateAsync(tenantId, code, ct);
        return Results.Created($"/api/admin/campaigns/{id}/dispositions/{created.Id}", MapToDispositionDto(created));
    }

    private static async Task<IResult> UpdateDisposition(
        long id,
        long codeId,
        HttpContext context,
        [FromBody] UpdateDispositionCodeRequest body,
        DispositionCodeStoreBase dispositionStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = (await dispositionStore.ListByCampaignAsync(tenantId, id, ct))
            .FirstOrDefault(d => d.Id == codeId);
        if (existing is null) return Results.NotFound();

        if (body.Label is not null) existing.Label = body.Label;
        if (body.Category is not null) existing.Category = ParseDispositionCategory(body.Category);
        if (body.IsSuccess.HasValue) existing.IsSuccess = body.IsSuccess.Value;
        if (body.TriggerRetry.HasValue) existing.TriggerRetry = body.TriggerRetry.Value;
        if (body.RetryDelayMinutes.HasValue) existing.RetryDelayMinutes = body.RetryDelayMinutes.Value;
        if (body.TriggerCallback.HasValue) existing.TriggerCallback = body.TriggerCallback.Value;
        if (body.IsActive.HasValue) existing.IsActive = body.IsActive.Value;
        if (body.SortOrder.HasValue) existing.SortOrder = body.SortOrder.Value;

        await dispositionStore.UpdateAsync(tenantId, existing, ct);
        return Results.Ok(MapToDispositionDto(existing));
    }

    private static async Task<IResult> DeleteDisposition(
        long id,
        long codeId,
        HttpContext context,
        DispositionCodeStoreBase dispositionStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await dispositionStore.DeleteAsync(tenantId, codeId, ct);
        return Results.NoContent();
    }

    // ─── Callback Handlers ────────────────────────────────────────────────────

    private static async Task<IResult> ListCallbacks(
        long id, HttpContext context, CampaignStoreBase store, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var callbacks = await store.GetPendingCallbacksAsync(tenantId, DateTimeOffset.UtcNow.AddDays(30), ct);
        var filtered = callbacks
            .Where(c => c.CampaignId == id)
            .Select(c => new CallbackDto(c.CampaignId, c.ContactId, c.ScheduledAt, c.AgentId))
            .ToList();
        return Results.Ok(filtered);
    }

    private static async Task<IResult> CreateCallback(
        long id, CreateCallbackRequest request, HttpContext context,
        CampaignStoreBase store, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (!DateTimeOffset.TryParse(request.ScheduledAt, out var scheduledAt))
            return Results.BadRequest("Invalid date format");
        await store.SaveCallbackAsync(tenantId, id, request.ContactId, scheduledAt, request.AgentId, ct);
        return Results.Created($"/api/admin/campaigns/{id}/callbacks", null);
    }

    // ─── Metrics Handlers ─────────────────────────────────────────────────────

    private static async Task<IResult> GetCampaignMetrics(
        long id,
        HttpContext context,
        CampaignStoreBase campaignStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var campaign = await campaignStore.GetCampaignAsync(tenantId, id, ct);
        if (campaign is null) return Results.NotFound();
        var snapshot = await campaignStore.GetCampaignMetricsAsync(tenantId, id, ct);
        return Results.Ok(MapToMetricsDto(snapshot, campaign));
    }

    private static async Task<IResult> ListActiveCampaignMetrics(
        HttpContext context,
        CampaignStoreBase campaignStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var snapshots = await campaignStore.GetActiveCampaignMetricsAsync(tenantId, ct);
        var campaigns = await campaignStore.GetActiveCampaignsAsync(tenantId, ct);
        var campaignMap = campaigns.ToDictionary(c => c.Id);
        return Results.Ok(snapshots.Select(s =>
        {
            campaignMap.TryGetValue(s.CampaignId, out var c);
            return MapToMetricsDto(s, c);
        }).ToList());
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
            CampaignStart: c.StartsAt?.ToString("O", CultureInfo.InvariantCulture),
            CampaignEnd: c.EndsAt?.ToString("O", CultureInfo.InvariantCulture),
            Schedule: c.ScheduleDays.Select(d => new ScheduleDayDto(
                Day: d.DayOfWeek.ToString(),
                Enabled: d.Enabled,
                Start: d.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                End: d.EndTime.ToString("HH:mm", CultureInfo.InvariantCulture))).ToArray(),
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
            StartsAt = req.CampaignStart is not null ? DateTimeOffset.Parse(req.CampaignStart, CultureInfo.InvariantCulture) : null,
            EndsAt = req.CampaignEnd is not null ? DateTimeOffset.Parse(req.CampaignEnd, CultureInfo.InvariantCulture) : null,
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

    private static ContactListDto MapToContactListDto(ContactList l) =>
        new(l.Id, l.Name, l.TotalContacts, l.PendingContacts, l.CompletedContacts, l.SourceFileName, l.CreatedAt);

    private static DispositionCodeDto MapToDispositionDto(DispositionCode d) =>
        new(d.Id, d.Code, d.Label, d.Category.ToString(), d.IsSuccess, d.TriggerRetry,
            d.RetryDelayMinutes, d.TriggerCallback, d.IsActive, d.SortOrder);

    private static DispositionCategory ParseDispositionCategory(string category) =>
        category.ToLowerInvariant() switch
        {
            "success" => DispositionCategory.Success,
            "failure" => DispositionCategory.Failure,
            "retry" => DispositionCategory.Retry,
            _ => DispositionCategory.SystemResult,
        };

    private static CampaignMetricsDto MapToMetricsDto(CampaignMetricsSnapshot s, Campaign? campaign) =>
        new(
            CampaignId: s.CampaignId,
            CampaignName: s.CampaignName,
            Status: s.Status.ToString(),
            ContactsDialed: s.ContactsDialed,
            ContactsRemaining: s.ContactsRemaining,
            ConnectRate: s.ConnectRate,
            AbandonRate: s.AbandonRate,
            ActiveCalls: 0,
            PacingRate: campaign?.PowerRatio ?? 1.0);

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

// ─── Contact List DTOs ─────────────────────────────────────────────────────────

internal sealed record CreateContactListRequest(string Name);
internal sealed record ContactListDto(long Id, string Name, int TotalContacts, int PendingContacts, int CompletedContacts, string? SourceFileName, DateTimeOffset CreatedAt);
internal sealed record ImportContactsRequest(ContactImportRowDto[] Contacts);
internal sealed record ContactImportRowDto(string FirstName, string? LastName, string Phone, string? PhoneType, Dictionary<string, string>? Metadata);
internal sealed record ImportResultDto(int Imported, int Skipped, int Duplicates);

// ─── Disposition DTOs ──────────────────────────────────────────────────────────

internal sealed record CreateDispositionCodeRequest(string Code, string Label, string Category, bool IsSuccess, bool TriggerRetry, int? RetryDelayMinutes, bool TriggerCallback);
internal sealed record UpdateDispositionCodeRequest(string? Label, string? Category, bool? IsSuccess, bool? TriggerRetry, int? RetryDelayMinutes, bool? TriggerCallback, bool? IsActive, int? SortOrder);
internal sealed record DispositionCodeDto(long Id, string Code, string Label, string Category, bool IsSuccess, bool TriggerRetry, int? RetryDelayMinutes, bool TriggerCallback, bool IsActive, int SortOrder);

// ─── Metrics DTOs ──────────────────────────────────────────────────────────────

internal sealed record CampaignMetricsDto(long CampaignId, string CampaignName, string Status, int ContactsDialed, int ContactsRemaining, double ConnectRate, double AbandonRate, int ActiveCalls, double PacingRate);

// ─── Callback DTOs ─────────────────────────────────────────────────────────────

internal sealed record CallbackDto(long CampaignId, long ContactId, DateTimeOffset ScheduledAt, string? AgentId);
internal sealed record CreateCallbackRequest(long ContactId, string Phone, string? AgentId, string ScheduledAt);
