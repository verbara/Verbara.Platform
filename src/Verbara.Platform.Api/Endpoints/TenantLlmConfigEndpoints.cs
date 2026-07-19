using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// Admin surface for a tenant's bring-your-own (BYO) LLM provider configuration (P2c.1).
/// <para>
/// <c>AdminOnly</c> + operational-tenant gated, and each route additionally requires the
/// <c>typification:ai:configure</c> permission (the same audience that edits a schema's AI config).
/// NO license gate: per-tenant BYO LLM is a baseline capability, not a licensed feature — the
/// shared global key is retired (BREAKING for multi-tenant), so every tenant that wants AI MUST
/// configure its own provider here.
/// </para>
/// <para>
/// The decrypted API key is NEVER returned: GET masks it to <c>keySet</c> + <c>keyLast4</c>. PUT
/// preserves the stored key when the request omits it (so a settings edit that doesn't rotate the
/// key keeps it). <c>POST /test</c> probes a SAVED or DRAFT config with a minimal completion; a
/// draft key is used in-memory only and is never persisted or logged.
/// </para>
/// </summary>
internal static class TenantLlmConfigEndpoints
{
    public static void MapTenantLlmConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/ai/llm-config")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant();

        group.MapGet("", GetConfig);
        group.MapPut("", UpsertConfig);
        group.MapDelete("", DeleteConfig);
        group.MapPost("/test", TestConnection);
    }

    // ─── GET — masked config (200 + { configured:false } when no row) ────────────

    private static async Task<Results<Ok<EmptyLlmConfigResponse>, Ok<TenantLlmConfigResponse>, ForbidHttpResult>> GetConfig(
        HttpContext context,
        [FromServices] ITenantLlmConfigStore store,
        [FromServices] PermissionResolver permissionResolver,
        [FromServices] IFeatureGateService featureGate,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var forbid = await EnsureConfigurePermissionAsync(context, permissionResolver, tenantId, ct);
        if (forbid is not null)
            return forbid;

        var available = featureGate.IsFeatureEnabled(tenantId.Value, PlanFeature.PlatformLlm);

        var config = await store.GetAsync(EntityId.From(tenantId.Value), ct);
        if (config is null)
            return TypedResults.Ok(new EmptyLlmConfigResponse(Configured: false, PlatformLlmAvailable: available));

        return TypedResults.Ok(TenantLlmConfigResponse.FromConfig(config, platformLlmAvailable: available));
    }

    // ─── PUT — upsert (preserve stored key when omitted) ─────────────────────────

    private static async Task<Results<Ok<TenantLlmConfigResponse>, ForbidHttpResult, BadRequest<ErrorResponse>, JsonHttpResult<ErrorResponse>>> UpsertConfig(
        HttpContext context,
        [FromBody] UpsertLlmConfigRequest body,
        [FromServices] ITenantLlmConfigStore store,
        [FromServices] ILlmProviderResolver resolver,
        [FromServices] PermissionResolver permissionResolver,
        [FromServices] IFeatureGateService featureGate,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var forbid = await EnsureConfigurePermissionAsync(context, permissionResolver, tenantId, ct);
        if (forbid is not null)
            return forbid;

        if (string.IsNullOrWhiteSpace(body.Model))
            return TypedResults.BadRequest(new ErrorResponse("Model is required."));

        // Platform-managed AI is a licensed entitlement: gate the opt-in on PlanFeature.PlatformLlm.
        // BYO (the default) is a baseline capability and stays ungated.
        var platformLlmAvailable = featureGate.IsFeatureEnabled(tenantId.Value, PlanFeature.PlatformLlm);
        if (body.AiSource == AiSource.PlatformManaged && !platformLlmAvailable)
        {
            return TypedResults.Json(
                new ErrorResponse("Platform-managed AI is not included in this tenant's plan."),
                ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden);
        }

        // Azure OpenAI builds a deployment-scoped URL (`/openai/deployments/{deployment}/chat/
        // completions?api-version={apiVersion}`); both segments are mandatory. Reject a malformed
        // Azure config at write time so no malformed URL can ever reach the provider at call time.
        if (body.ProviderType == ProviderType.AzureOpenAi)
        {
            if (string.IsNullOrWhiteSpace(body.Settings?.AzureDeployment))
                return TypedResults.BadRequest(new ErrorResponse("settings.azureDeployment is required for the AzureOpenAi provider."));
            if (string.IsNullOrWhiteSpace(body.Settings?.AzureApiVersion))
                return TypedResults.BadRequest(new ErrorResponse("settings.azureApiVersion is required for the AzureOpenAi provider."));
        }

        var entityTenantId = EntityId.From(tenantId.Value);
        var existing = await store.GetAsync(entityTenantId, ct);

        // Key handling: a non-empty incoming key sets/rotates it; an omitted key PRESERVES the
        // stored key (the masked GET never round-trips the real key, so the client omits it on
        // edits that don't rotate). last4 tracks the EFFECTIVE key.
        string? effectiveKey;
        string? effectiveLast4;
        if (!string.IsNullOrEmpty(body.ApiKey))
        {
            effectiveKey = body.ApiKey;
            effectiveLast4 = Last4(body.ApiKey);
        }
        else
        {
            effectiveKey = existing?.ApiKey;
            effectiveLast4 = existing?.ApiKeyLast4;
        }

        var now = clock.UtcNow;
        var config = new TenantLlmConfig
        {
            TenantId = entityTenantId,
            ProviderType = body.ProviderType,
            Model = body.Model,
            ApiKey = effectiveKey,
            ApiKeyLast4 = effectiveLast4,
            Settings = body.Settings ?? new ProviderSettings(),
            Enabled = body.Enabled,
            AiSource = body.AiSource,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        };

        await store.UpsertAsync(config, ct);
        resolver.Invalidate(entityTenantId);

        return TypedResults.Ok(TenantLlmConfigResponse.FromConfig(config, platformLlmAvailable: platformLlmAvailable));
    }

    // ─── DELETE — remove the row (back to manual/agent-driven mode) ──────────────

    private static async Task<IResult> DeleteConfig(
        HttpContext context,
        [FromServices] ITenantLlmConfigStore store,
        [FromServices] ILlmProviderResolver resolver,
        [FromServices] PermissionResolver permissionResolver,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var forbid = await EnsureConfigurePermissionAsync(context, permissionResolver, tenantId, ct);
        if (forbid is not null)
            return forbid;

        var entityTenantId = EntityId.From(tenantId.Value);
        await store.DeleteAsync(entityTenantId, ct);
        resolver.Invalidate(entityTenantId);

        return Results.NoContent();
    }

    // ─── POST /test — probe saved or draft config ───────────────────────────────

    private static async Task<Results<Ok<TestLlmConnectionResponse>, ForbidHttpResult>> TestConnection(
        HttpContext context,
        [FromBody] TestLlmConnectionRequest body,
        [FromServices] ITenantLlmConfigStore store,
        [FromServices] ILlmProviderResolver resolver,
        [FromServices] PermissionResolver permissionResolver,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var forbid = await EnsureConfigurePermissionAsync(context, permissionResolver, tenantId, ct);
        if (forbid is not null)
            return forbid;

        var entityTenantId = EntityId.From(tenantId.Value);
        var saved = await store.GetAsync(entityTenantId, ct);

        // Build the probe config: a DRAFT (any field present in the body) is built in-memory only;
        // otherwise the SAVED config is probed. A draft that omits the key MAY reuse the stored key —
        // but ONLY when the draft probes the SAME destination (base url + provider type) as the saved
        // config. If the draft OVERRIDES the destination, falling back to the stored key would let an
        // admin exfiltrate the decrypted provider credential to an arbitrary endpoint, so we require
        // an explicit key in that case. NOTHING from the draft is persisted or logged.
        var isDraft = body.ProviderType is not null
            || !string.IsNullOrEmpty(body.Model)
            || !string.IsNullOrEmpty(body.ApiKey)
            || body.Settings is not null;

        TenantLlmConfig? probe;
        if (isDraft)
        {
            var now = clock.UtcNow;

            // Does the draft point at a DIFFERENT destination than the saved config? A different base
            // url OR a different provider type means the stored key would be sent somewhere new.
            var overridesBaseUrl = body.Settings?.BaseUrl is not null
                && !string.Equals(body.Settings.BaseUrl, saved?.Settings.BaseUrl, StringComparison.Ordinal);
            var overridesProviderType = body.ProviderType is not null
                && body.ProviderType != saved?.ProviderType;
            var overridesDestination = overridesBaseUrl || overridesProviderType;

            if (overridesDestination && string.IsNullOrEmpty(body.ApiKey))
            {
                // Refuse to send the stored key to a different provider/endpoint — never the stored key.
                return TypedResults.Ok(new TestLlmConnectionResponse(
                    Reachable: false, AuthOk: false, ModelOk: false, LatencyMs: 0,
                    Error: "An explicit apiKey is required when testing a different provider or endpoint than the saved configuration."));
            }

            // Reuse the saved key ONLY when the destination matches; otherwise an explicit key was supplied above.
            var draftKey = !string.IsNullOrEmpty(body.ApiKey) ? body.ApiKey : saved?.ApiKey;
            probe = new TenantLlmConfig
            {
                TenantId = entityTenantId,
                ProviderType = body.ProviderType ?? saved?.ProviderType ?? ProviderType.OpenAiCompatible,
                Model = !string.IsNullOrEmpty(body.Model) ? body.Model : saved?.Model ?? string.Empty,
                ApiKey = draftKey,
                ApiKeyLast4 = null,
                Settings = body.Settings ?? saved?.Settings ?? new ProviderSettings(),
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }
        else
        {
            probe = saved;
        }

        if (probe is null || string.IsNullOrWhiteSpace(probe.Model))
        {
            return TypedResults.Ok(new TestLlmConnectionResponse(
                Reachable: false, AuthOk: false, ModelOk: false, LatencyMs: 0,
                Error: "No provider configured to test. Save a provider or supply a draft in the request body."));
        }

        var provider = resolver.BuildTransient(probe);
        var request = new LlmRequest(
            Messages: [new LlmMessage("user", "ping")],
            Temperature: 0.0,
            MaxTokens: 1);

        var sw = Stopwatch.StartNew();
        try
        {
            await provider.CompleteAsync(request, ct);
            sw.Stop();
            return TypedResults.Ok(new TestLlmConnectionResponse(
                Reachable: true, AuthOk: true, ModelOk: true, LatencyMs: sw.ElapsedMilliseconds, Error: null));
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            // 401/403 → reachable + authenticated FAILED. Any other status / transport failure →
            // not necessarily an auth problem. Status null = transport-level failure (DNS/connect).
            var authFailed = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            var reachable = ex.StatusCode is not null;
            return TypedResults.Ok(new TestLlmConnectionResponse(
                Reachable: reachable,
                AuthOk: reachable && !authFailed,
                ModelOk: false,
                LatencyMs: sw.ElapsedMilliseconds,
                Error: DescribeHttpError(ex)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return TypedResults.Ok(new TestLlmConnectionResponse(
                Reachable: false, AuthOk: false, ModelOk: false, LatencyMs: sw.ElapsedMilliseconds,
                Error: ex.GetType().Name));
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    // Never let the hint be the whole secret: a key shorter than 4 chars has no safe last-4 to show.
    private static string? Last4(string key) =>
        key.Length >= 4 ? key[^4..] : null;

    /// <summary>A short, secret-free description of an HTTP error from a probe (status only, never the body).</summary>
    private static string DescribeHttpError(HttpRequestException ex) =>
        ex.StatusCode is { } status
            ? $"HTTP {(int)status} {status}"
            : "Connection failed (endpoint unreachable).";

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }

    /// <summary>
    /// Returns a <c>TypedResults.Forbid()</c> result when the caller lacks
    /// <c>typification:ai:configure</c>; <see langword="null"/> when authorized. The caller user id
    /// is resolved in the canonical order (<c>user_id</c> ?? NameIdentifier ?? <c>sub</c>) so an
    /// API-key caller's owning-user id wins over the key id (copied from <c>TypificationEndpoints</c>).
    /// </summary>
    private static async Task<ForbidHttpResult?> EnsureConfigurePermissionAsync(
        HttpContext context,
        PermissionResolver permissionResolver,
        TenantId tenantId,
        CancellationToken ct)
    {
        var callerUserId = context.User.FindFirstValue("user_id")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");

        IReadOnlySet<string> perms = string.IsNullOrEmpty(callerUserId)
            ? new HashSet<string>(StringComparer.Ordinal)
            : await permissionResolver.ResolveAsync(tenantId, EntityId.From(callerUserId), ct);

        return PermissionResolver.HasPermission(perms, "typification:ai:configure")
            ? null
            : TypedResults.Forbid();
    }
}
