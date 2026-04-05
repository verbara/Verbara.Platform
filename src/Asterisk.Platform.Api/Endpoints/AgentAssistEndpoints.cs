using Asterisk.Platform.Api.Middleware;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.AgentAssist.Options;
using Asterisk.Sdk.Pro.AgentAssist.Storage.Postgres.Stores;
using Asterisk.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Endpoints;

internal static class AgentAssistEndpoints
{
    public static void MapAgentAssistEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Session query endpoints (SupervisorPlus) ───────────────────────────
        var sessions = app.MapGroup("/agent-assist/sessions")
            .RequireAuthorization("SupervisorPlus")
            .RequireLicenseFeature(LicenseFeature.AgentAssist);
        sessions.MapGet("/{sessionId}", GetSession);
        sessions.MapGet("/{sessionId}/suggestions", GetSessionSuggestions);
        sessions.MapGet("/{sessionId}/compliance", GetSessionCompliance);

        // ── Admin config endpoints (AdminOnly) ─────────────────────────────────
        var admin = app.MapGroup("/admin/agent-assist")
            .RequireAuthorization("AdminOnly")
            .RequireLicenseFeature(LicenseFeature.AgentAssist);
        admin.MapGet("/config", GetConfig);
        admin.MapPut("/config", UpdateConfig);
        admin.MapGet("/keyword-rules", GetKeywordRules);
        admin.MapPut("/keyword-rules", UpdateKeywordRules);
        admin.MapGet("/compliance-rules", GetComplianceRules);
        admin.MapPut("/compliance-rules", UpdateComplianceRules);
    }

    // ─── Session Handlers ─────────────────────────────────────────────────────

    private static async Task<IResult> GetSession(
        string sessionId,
        HttpContext context,
        [FromServices] AgentAssistSessionStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var from = DateTimeOffset.UtcNow.AddDays(-30);
        var until = DateTimeOffset.UtcNow;

        var sessions = await store.QueryByTenantAsync(tenantId, from, until, ct);
        var session = sessions.FirstOrDefault(s => s.SessionId == sessionId);

        if (session is null)
            return Results.NotFound();

        return Results.Ok(MapSessionToDto(session));
    }

    private static async Task<IResult> GetSessionSuggestions(
        string sessionId,
        [FromServices] SuggestionLogStore store,
        CancellationToken ct)
    {
        var rows = await store.QueryBySessionAsync(sessionId, ct);
        var dtos = rows.Select(r => new SuggestionLogRowDto(
            r.Id,
            r.SessionId,
            r.TenantId,
            r.EmittedAt,
            r.Priority,
            r.Source,
            r.TriggerPhrase,
            r.SuggestionText,
            r.Whispered)).ToArray();

        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetSessionCompliance(
        string sessionId,
        [FromServices] ComplianceAlertStore store,
        CancellationToken ct)
    {
        var rows = await store.QueryBySessionAsync(sessionId, ct);
        var dtos = rows.Select(r => new ComplianceAlertRowDto(
            r.Id,
            r.SessionId,
            r.TenantId,
            r.OccurredAt,
            r.RuleId,
            r.Phrase,
            r.Severity)).ToArray();

        return Results.Ok(dtos);
    }

    // ─── Admin Config Handlers ────────────────────────────────────────────────

    private static IResult GetConfig(
        [FromServices] IOptions<AgentAssistOptions> options,
        [FromServices] AgentAssistConfigStore configStore)
    {
        var snapshot = configStore.GetSnapshot() ?? BuildSnapshot(options.Value);
        return Results.Ok(snapshot);
    }

    private static IResult UpdateConfig(
        AgentAssistConfigSnapshot body,
        [FromServices] AgentAssistConfigStore configStore)
    {
        configStore.Update(body);
        return Results.Ok(body);
    }

    private static IResult GetKeywordRules([FromServices] AgentAssistConfigStore configStore)
    {
        return Results.Ok(configStore.GetKeywordRules());
    }

    private static IResult UpdateKeywordRules(
        KeywordRuleDto[] rules,
        [FromServices] AgentAssistConfigStore configStore)
    {
        configStore.UpdateKeywordRules(rules);
        return Results.Ok(rules);
    }

    private static IResult GetComplianceRules([FromServices] AgentAssistConfigStore configStore)
    {
        return Results.Ok(configStore.GetComplianceRules());
    }

    private static IResult UpdateComplianceRules(
        ComplianceRuleDto[] rules,
        [FromServices] AgentAssistConfigStore configStore)
    {
        configStore.UpdateComplianceRules(rules);
        return Results.Ok(rules);
    }

    // ─── Mapping Helpers ──────────────────────────────────────────────────────

    private static AgentAssistSessionDto MapSessionToDto(AgentAssistSessionRow row) =>
        new(
            row.SessionId,
            row.TenantId,
            row.QueueName,
            row.AgentId,
            row.StartedAt,
            row.EndedAt,
            row.SuggestionCount,
            row.ComplianceAlerts,
            row.FinalSentiment);

    private static AgentAssistConfigSnapshot BuildSnapshot(AgentAssistOptions opts) =>
        new(
            AudioSocketPort: opts.AudioSocketPort,
            SuggestionTimeoutMs: (int)opts.SuggestionTimeout.TotalMilliseconds,
            SentimentTimeoutMs: (int)opts.SentimentTimeout.TotalMilliseconds,
            ComplianceTimeoutMs: (int)opts.ComplianceTimeout.TotalMilliseconds,
            MaxHistorySegments: opts.MaxHistorySegments,
            InactivityTimeoutMinutes: (int)opts.InactivityTimeout.TotalMinutes,
            WhisperEnabled: opts.Whisper.Enabled,
            WhisperThreshold: opts.Whisper.Threshold.ToString(),
            FilterQueueNames: [.. opts.Filters.QueueNames],
            FilterAgentIds: [.. opts.Filters.AgentIds]);

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── In-Memory Config Store (singleton, mutable) ──────────────────────────────

/// <summary>Holds a mutable in-memory snapshot of AgentAssist configuration for the admin API.</summary>
internal sealed class AgentAssistConfigStore
{
    private AgentAssistConfigSnapshot? _snapshot;
    private KeywordRuleDto[] _keywordRules = [];
    private ComplianceRuleDto[] _complianceRules = [];

    public AgentAssistConfigSnapshot? GetSnapshot() => _snapshot;

    public void Update(AgentAssistConfigSnapshot snapshot) => _snapshot = snapshot;

    public KeywordRuleDto[] GetKeywordRules() => _keywordRules;

    public void UpdateKeywordRules(KeywordRuleDto[] rules) => _keywordRules = rules;

    public ComplianceRuleDto[] GetComplianceRules() => _complianceRules;

    public void UpdateComplianceRules(ComplianceRuleDto[] rules) => _complianceRules = rules;
}

// ─── Session DTOs ─────────────────────────────────────────────────────────────

internal sealed record AgentAssistSessionDto(
    string SessionId,
    string TenantId,
    string? QueueName,
    string? AgentId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int SuggestionCount,
    int ComplianceAlerts,
    float? FinalSentiment);

internal sealed record SuggestionLogRowDto(
    long Id,
    string SessionId,
    string TenantId,
    DateTimeOffset EmittedAt,
    string Priority,
    string Source,
    string? TriggerPhrase,
    string SuggestionText,
    bool Whispered);

internal sealed record ComplianceAlertRowDto(
    long Id,
    string SessionId,
    string TenantId,
    DateTimeOffset OccurredAt,
    string RuleId,
    string? Phrase,
    string Severity);

// ─── Config DTOs ──────────────────────────────────────────────────────────────

internal sealed record AgentAssistConfigSnapshot(
    int AudioSocketPort,
    int SuggestionTimeoutMs,
    int SentimentTimeoutMs,
    int ComplianceTimeoutMs,
    int MaxHistorySegments,
    int InactivityTimeoutMinutes,
    bool WhisperEnabled,
    string WhisperThreshold,
    string[] FilterQueueNames,
    string[] FilterAgentIds);

internal sealed record KeywordRuleDto(
    string RuleId,
    string Keyword,
    string Priority,
    string? SuggestionText);

internal sealed record ComplianceRuleDto(
    string RuleId,
    string Pattern,
    string Severity,
    string Action,
    string? Description);
