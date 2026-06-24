using Verbara.Platform.Llm;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// HTTP-safe projection of a tenant's <see cref="TenantLlmConfig"/> (P2c.1). The decrypted API key
/// is NEVER returned: it is masked to <see cref="KeySet"/> (is a key configured?) +
/// <see cref="KeyLast4"/> (a non-secret display hint), mirroring the
/// <c>TenantAuthConfigResponse</c> reveal-once/fingerprint idiom.
/// </summary>
/// <param name="ProviderType">The provider family discriminator (<c>OpenAiCompatible</c> / <c>AzureOpenAi</c> / <c>Anthropic</c>).</param>
/// <param name="Model">The configured model identifier.</param>
/// <param name="Settings">Type-specific (non-secret) provider settings.</param>
/// <param name="Enabled">Whether AI is enabled for this tenant.</param>
/// <param name="AiSource">BYO (tenant key) vs platform-managed (Verbara operator key, metered in AI Credits).</param>
/// <param name="PlatformLlmAvailable">Whether the tenant's plan entitles it to the platform-managed LLM (<c>PlanFeature.PlatformLlm</c>).</param>
/// <param name="KeySet">Whether an API key is currently stored (the key value is never returned).</param>
/// <param name="KeyLast4">Last 4 chars of the stored key (non-secret display hint), or <see langword="null"/>.</param>
/// <param name="UpdatedAt">UTC timestamp of the most recent upsert.</param>
public sealed record TenantLlmConfigResponse(
    ProviderType ProviderType,
    string Model,
    ProviderSettings Settings,
    bool Enabled,
    AiSource AiSource,
    bool PlatformLlmAvailable,
    bool KeySet,
    string? KeyLast4,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Projects a stored <see cref="TenantLlmConfig"/> to the masked HTTP response.</summary>
    /// <param name="config">The stored config to project.</param>
    /// <param name="platformLlmAvailable">Whether the tenant's plan entitles it to the platform-managed LLM.</param>
    public static TenantLlmConfigResponse FromConfig(TenantLlmConfig config, bool platformLlmAvailable)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new TenantLlmConfigResponse(
            ProviderType: config.ProviderType,
            Model: config.Model,
            Settings: config.Settings,
            Enabled: config.Enabled,
            AiSource: config.AiSource,
            PlatformLlmAvailable: platformLlmAvailable,
            // KeySet reflects ACTUAL key presence — not the last4 display hint (which is an
            // independent, non-secret hint that may be absent even when a key is set).
            KeySet: !string.IsNullOrEmpty(config.ApiKey),
            KeyLast4: config.ApiKeyLast4,
            UpdatedAt: config.UpdatedAt);
    }
}

/// <summary>
/// The "no provider configured" GET response (P2c.1). Returned with HTTP 200 (NOT 404) because a
/// tenant with no LLM provider is a valid, non-error state: AI is strictly opt-in and the tenant
/// simply runs manual/agent-driven typification.
/// </summary>
/// <param name="Configured">Always <see langword="false"/> — the discriminator the Web client switches on.</param>
/// <param name="PlatformLlmAvailable">Whether the tenant's plan entitles it to the platform-managed LLM (<c>PlanFeature.PlatformLlm</c>).</param>
public sealed record EmptyLlmConfigResponse(bool Configured, bool PlatformLlmAvailable);

/// <summary>
/// Upsert request for a tenant's LLM config (P2c.1, <c>PUT /admin/ai/llm-config</c>).
/// </summary>
/// <param name="ProviderType">The provider family to configure.</param>
/// <param name="Model">The model identifier (e.g. <c>gpt-4o-mini</c>).</param>
/// <param name="ApiKey">
/// The plaintext API key. When <see langword="null"/> or empty on a tenant that already has a
/// stored key, the stored key is PRESERVED (the masked GET never round-trips the real key, so the
/// Web client omits it on edits that don't rotate the key). A non-empty value sets/rotates the key.
/// </param>
/// <param name="Settings">Type-specific provider settings (base URL, Azure deployment/version, Anthropic version).</param>
/// <param name="Enabled">Whether AI is enabled for this tenant.</param>
/// <param name="AiSource">
/// BYO (tenant key, default) vs platform-managed (Verbara operator key, metered in AI Credits).
/// Defaulted to <see cref="AiSource.Byo"/> so existing BYO callers stay source-compatible.
/// <see cref="AiSource.PlatformManaged"/> requires the <c>PlanFeature.PlatformLlm</c> entitlement.
/// </param>
public sealed record UpsertLlmConfigRequest(
    ProviderType ProviderType,
    string Model,
    string? ApiKey,
    ProviderSettings? Settings,
    bool Enabled,
    AiSource AiSource = AiSource.Byo);

/// <summary>
/// "Test connection" request (P2c.1, <c>POST /admin/ai/llm-config/test</c>). When all fields are
/// <see langword="null"/> the SAVED config is probed; otherwise a DRAFT config is built from the
/// body and probed in-memory only (its key is never persisted or logged).
/// </summary>
/// <param name="ProviderType">Draft provider family, or <see langword="null"/> to probe the saved config.</param>
/// <param name="Model">Draft model identifier.</param>
/// <param name="ApiKey">
/// Draft plaintext API key, used only in-memory for the probe. When <see langword="null"/>/empty on
/// a draft probe of a tenant that already has a stored key, the stored key is reused for the probe.
/// </param>
/// <param name="Settings">Draft type-specific provider settings.</param>
public sealed record TestLlmConnectionRequest(
    ProviderType? ProviderType,
    string? Model,
    string? ApiKey,
    ProviderSettings? Settings);

/// <summary>
/// "Test connection" result (P2c.1). A reachable provider that accepted the credentials and
/// returned a completion for the configured model is fully green (<see cref="Reachable"/> +
/// <see cref="AuthOk"/> + <see cref="ModelOk"/> all <see langword="true"/>).
/// </summary>
/// <param name="Reachable">The endpoint was reachable (the HTTP request completed without a transport failure).</param>
/// <param name="AuthOk">The credentials were accepted (no 401/403).</param>
/// <param name="ModelOk">The model produced a completion (the request succeeded end-to-end).</param>
/// <param name="LatencyMs">Round-trip latency of the probe in milliseconds.</param>
/// <param name="Error">A short, secret-free error summary when the probe failed; <see langword="null"/> on success.</param>
public sealed record TestLlmConnectionResponse(
    bool Reachable,
    bool AuthOk,
    bool ModelOk,
    long LatencyMs,
    string? Error);
