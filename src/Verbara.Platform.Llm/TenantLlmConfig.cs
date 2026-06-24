using System.Text.Json.Serialization;
using Verbara.Platform.Core;

namespace Verbara.Platform.Llm;

/// <summary>
/// The LLM provider family a tenant configures for BYO (bring-your-own) typification AI.
/// The discriminator persisted in <c>tenant_llm_config.provider_type</c> and switched on by
/// the resolver to select the concrete <see cref="ILlmProvider"/> implementation (P2c.1).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderType>))]
public enum ProviderType
{
    /// <summary>OpenAI-compatible Chat Completions API (<c>Authorization: Bearer</c>).</summary>
    OpenAiCompatible = 0,

    /// <summary>Azure OpenAI (deployment-scoped URL + <c>api-key</c> header + <c>api-version</c>).</summary>
    AzureOpenAi = 1,

    /// <summary>Anthropic Messages API (<c>x-api-key</c> + <c>anthropic-version</c>).</summary>
    Anthropic = 2,
}

/// <summary>
/// Type-specific provider configuration, persisted as a single JSONB column
/// (<c>tenant_llm_config.provider_settings</c>) and source-gen serialized (AOT).
/// Extensible: a new provider type adds a nullable field here — code only, no DB migration.
/// </summary>
public sealed record ProviderSettings
{
    /// <summary>Base URL for <see cref="ProviderType.OpenAiCompatible"/> / Azure resource endpoint.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Azure OpenAI deployment name (<see cref="ProviderType.AzureOpenAi"/>).</summary>
    public string? AzureDeployment { get; init; }

    /// <summary>Azure OpenAI <c>api-version</c> query parameter (<see cref="ProviderType.AzureOpenAi"/>).</summary>
    public string? AzureApiVersion { get; init; }

    /// <summary>Anthropic <c>anthropic-version</c> header value (<see cref="ProviderType.Anthropic"/>); default applies when null.</summary>
    public string? AnthropicVersion { get; init; }
}

/// <summary>
/// A single tenant's BYO LLM provider configuration (P2c.1). One row per tenant (MVP),
/// stored in <c>tenant_llm_config</c>. The decrypted <see cref="ApiKey"/> is materialised
/// only server-side inside the resolver at use time — it is NEVER serialized to clients.
/// "No config" / <c>Enabled == false</c> are valid, non-error states (AI is strictly opt-in).
/// </summary>
public sealed class TenantLlmConfig
{
    /// <summary>Tenant that owns this config (primary key).</summary>
    public required EntityId TenantId { get; init; }

    /// <summary>The provider family (discriminator).</summary>
    public required ProviderType ProviderType { get; init; }

    /// <summary>Model identifier, e.g. <c>gpt-4o-mini</c> or <c>claude-3-5-haiku-latest</c>.</summary>
    public required string Model { get; init; }

    /// <summary>
    /// Decrypted API key (server-only). Populated on read by the Postgres store via
    /// <c>IDataProtector.Unprotect</c>; supplied in plaintext on upsert and encrypted at rest.
    /// This value is NEVER serialized to clients — the API masks it to <c>keySet</c> + <see cref="ApiKeyLast4"/>.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>Non-secret display hint (last 4 chars of the key), set on upsert for masked GET views.</summary>
    public string? ApiKeyLast4 { get; init; }

    /// <summary>Type-specific provider configuration (JSONB column).</summary>
    public ProviderSettings Settings { get; init; } = new();

    /// <summary>Whether AI is enabled for this tenant. Default <see langword="false"/> (opt-in).</summary>
    public bool Enabled { get; init; }

    /// <summary>BYO (tenant key) vs platform-managed (Verbara operator key). Defaults to <see cref="AiSource.Byo"/>.</summary>
    public AiSource AiSource { get; init; }

    /// <summary>UTC timestamp when the config was first created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC timestamp of the most recent upsert.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
