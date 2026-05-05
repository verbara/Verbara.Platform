using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.AgentAssist.Features;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// Runtime feature-management surface for AgentAssist. Lets a platform-admin toggle the
/// engine on/off and rotate STT provider credentials without restarting the API host.
/// Credentials are never returned to the client (redacted to <c>"***"</c>) and never
/// captured in audit rows — only the enabled flag + provider name land in the audit trail.
/// </summary>
internal static class AgentAssistFeatureEndpoints
{
    private static readonly HashSet<string> AllowedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "deepgram", "whisper", "azure-whisper", "google",
    };

    public static void MapAgentAssistFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/features/agent-assist")
            .RequireAuthorization("features:agent-assist:manage");

        group.MapGet("", GetFeature);
        group.MapPut("", UpdateFeature);
    }

    private static async Task<IResult> GetFeature(
        [FromServices] IAgentAssistFeatureToggle toggle,
        CancellationToken ct)
    {
        var state = await toggle.GetCurrentAsync(ct);
        return Results.Ok(ToDto(state));
    }

    private static async Task<IResult> UpdateFeature(
        HttpContext context,
        [FromBody] AgentAssistFeatureUpdateRequest body,
        [FromServices] IAgentAssistFeatureToggle toggle,
        [FromServices] AgentAssistCredentialsProtector protector,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IAuditService audit,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        // Validate provider when enabling. Normalize first, then whitelist — ensures
        // the contract ("state.Provider is always lowercase") matches the check surface.
        string? normalizedProvider = null;
        if (body.Enabled)
        {
            var candidate = body.Provider?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(candidate)
                || !AllowedProviders.Contains(candidate))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["provider"] = ["Provider must be one of: deepgram, whisper, azure-whisper, google."],
                });
            }
            normalizedProvider = candidate;

            // Provider-specific credential requirements
            var creds = body.Credentials ?? new AgentAssistCredentialsDto(null, null);
            var missing = ValidateCredentials(normalizedProvider, creds);
            if (missing is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["credentials"] = [missing],
                });
            }
        }

        var previous = await toggle.GetCurrentAsync(ct);

        // Encrypt credentials before they enter the toggle state
        IReadOnlyDictionary<string, string>? encrypted = null;
        if (body.Enabled && body.Credentials is not null)
        {
            var plain = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(body.Credentials.ApiKey))
                plain["apiKey"] = body.Credentials.ApiKey!;
            if (!string.IsNullOrWhiteSpace(body.Credentials.Endpoint))
                plain["endpoint"] = body.Credentials.Endpoint!;
            if (plain.Count > 0)
                encrypted = protector.Protect(plain);
        }

        var actor = context.User.FindFirst("sub")?.Value
                    ?? context.User.FindFirst("user_id")?.Value
                    ?? "system";

        var next = new AgentAssistFeatureState(
            Enabled: body.Enabled,
            Provider: normalizedProvider,
            ProtectedCredentials: encrypted,
            UpdatedAt: clock.UtcNow,
            UpdatedBy: actor);

        var persisted = await toggle.SetAsync(next, ct);

        // Audit — credentials NEVER included, only metadata
        var host = await tenantStore.GetHostTenantAsync(ct);
        var auditTenant = host is not null ? new TenantId(host.TenantId) : new TenantId("platform");
        await audit.RecordAsync(
            auditTenant,
            category: "admin",
            action: "agentassist.config.changed",
            severity: "warning",
            actorId: actor,
            actorType: "user",
            targetId: "agent-assist",
            targetType: "feature",
            changes: new AuditChanges(
                Before: new { previous.Enabled, previous.Provider },
                After: new { persisted.Enabled, persisted.Provider }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);

        return Results.Ok(ToDto(persisted));
    }

    private static AgentAssistFeatureDto ToDto(AgentAssistFeatureState state)
    {
        // Surfaces whether credentials are present (so the UI can show "configured" state)
        // without leaking any part of the ciphertext.
        var hasCreds = state.ProtectedCredentials is { Count: > 0 };
        return new AgentAssistFeatureDto(
            Enabled: state.Enabled,
            Provider: state.Provider,
            CredentialsConfigured: hasCreds,
            ApiKeyMasked: hasCreds ? "***" : null,
            EndpointMasked: hasCreds && state.ProtectedCredentials!.ContainsKey("endpoint") ? "***" : null,
            UpdatedAt: state.UpdatedAt,
            UpdatedBy: state.UpdatedBy);
    }

    private static string? ValidateCredentials(string provider, AgentAssistCredentialsDto creds)
    {
        return provider switch
        {
            "deepgram" => string.IsNullOrWhiteSpace(creds.ApiKey) ? "Deepgram requires apiKey." : null,
            "whisper" => string.IsNullOrWhiteSpace(creds.ApiKey) ? "Whisper requires apiKey." : null,
            "azure-whisper" =>
                string.IsNullOrWhiteSpace(creds.ApiKey) || string.IsNullOrWhiteSpace(creds.Endpoint)
                    ? "Azure Whisper requires both apiKey and endpoint."
                    : null,
            "google" => string.IsNullOrWhiteSpace(creds.ApiKey)
                ? "Google requires apiKey (service-account JSON)."
                : null,
            _ => "Unknown provider.",
        };
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record AgentAssistFeatureDto(
    bool Enabled,
    string? Provider,
    bool CredentialsConfigured,
    string? ApiKeyMasked,
    string? EndpointMasked,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

internal sealed record AgentAssistFeatureUpdateRequest(
    bool Enabled,
    string? Provider,
    AgentAssistCredentialsDto? Credentials);

internal sealed record AgentAssistCredentialsDto(
    string? ApiKey,
    string? Endpoint);
