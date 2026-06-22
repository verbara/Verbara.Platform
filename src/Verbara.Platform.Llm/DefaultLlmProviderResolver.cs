using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Llm;

/// <summary>
/// Default <see cref="ILlmProviderResolver"/> (P2c.1, Architecture A). Reads the tenant's
/// <see cref="TenantLlmConfig"/> from the store, switches on <see cref="ProviderType"/> to build the
/// matching provider over an <see cref="IHttpClientFactory"/> named client, and caches the result
/// per tenant keyed on a stable fingerprint of the config so a changed config rebuilds automatically.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class DefaultLlmProviderResolver : ILlmProviderResolver
{
    /// <summary>Named <see cref="HttpClient"/> for the OpenAI-compatible provider.</summary>
    public const string OpenAiClientName = "llm.openai_compatible";

    /// <summary>Named <see cref="HttpClient"/> for the Azure OpenAI provider.</summary>
    public const string AzureClientName = "llm.azure_openai";

    /// <summary>Named <see cref="HttpClient"/> for the Anthropic provider.</summary>
    public const string AnthropicClientName = "llm.anthropic";

    private readonly ITenantLlmConfigStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ResiliencePolicy? _policy;
    private readonly IMeterFactory? _meterFactory;
    private readonly ILoggerFactory? _loggerFactory;

    // tenantId.Value → (fingerprint, resolved). One entry per tenant; Invalidate evicts by tenant.
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public DefaultLlmProviderResolver(
        ITenantLlmConfigStore store,
        IHttpClientFactory httpFactory,
        IServiceProvider serviceProvider,
        IMeterFactory? meterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _store = store;
        _httpFactory = httpFactory;
        _policy = serviceProvider.GetKeyedService<ResiliencePolicy>(
            OpenAiCompatibleLlmProvider.ResiliencePolicyKey);
        _meterFactory = meterFactory;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public async Task<ResolvedLlmProvider?> ResolveAsync(EntityId tenantId, CancellationToken ct)
    {
        var config = await _store.GetAsync(tenantId, ct).ConfigureAwait(false);
        if (config is null || !config.Enabled)
        {
            // Fail-closed: no config / disabled is a valid "AI off" state. Drop any stale entry.
            _cache.TryRemove(tenantId.Value, out _);
            return null;
        }

        var fingerprint = ComputeFingerprint(config);

        if (_cache.TryGetValue(tenantId.Value, out var cached) && cached.Fingerprint == fingerprint)
            return cached.Resolved;

        var resolved = Build(config);
        _cache[tenantId.Value] = new CacheEntry(fingerprint, resolved);
        return resolved;
    }

    /// <inheritdoc />
    public void Invalidate(EntityId tenantId) => _cache.TryRemove(tenantId.Value, out _);

    private ResolvedLlmProvider Build(TenantLlmConfig config) =>
        new(BuildTransient(config), config.Model);

    /// <inheritdoc />
    public ILlmProvider BuildTransient(TenantLlmConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var effective = new LlmEffectiveOptions(
            BaseUrl: config.Settings.BaseUrl,
            ApiKey: config.ApiKey,
            Model: config.Model,
            Temperature: DefaultTemperature,
            MaxTokens: DefaultMaxTokens,
            TimeoutSeconds: DefaultTimeoutSeconds);

        return config.ProviderType switch
        {
            ProviderType.AzureOpenAi => new AzureOpenAiLlmProvider(
                _httpFactory.CreateClient(AzureClientName),
                effective,
                config.Settings.AzureDeployment ?? string.Empty,
                config.Settings.AzureApiVersion ?? string.Empty,
                _policy,
                _meterFactory,
                _loggerFactory?.CreateLogger<AzureOpenAiLlmProvider>()),

            ProviderType.Anthropic => new AnthropicLlmProvider(
                _httpFactory.CreateClient(AnthropicClientName),
                effective,
                config.Settings.AnthropicVersion,
                _policy,
                _meterFactory,
                _loggerFactory?.CreateLogger<AnthropicLlmProvider>()),

            _ => new OpenAiCompatibleLlmProvider(
                _httpFactory.CreateClient(OpenAiClientName),
                effective,
                _policy,
                _meterFactory,
                _loggerFactory?.CreateLogger<OpenAiCompatibleLlmProvider>()),
        };
    }

    /// <summary>
    /// A stable fingerprint of the persisted config — provider type, model, settings, enabled flag,
    /// and a key fingerprint (last 4 / length, never the key itself). Used as the cache version token
    /// so a config change rebuilds the provider without a cryptographic hash.
    /// </summary>
    private static string ComputeFingerprint(TenantLlmConfig config)
    {
        var s = config.Settings;
        var keyFingerprint = config.ApiKey is { Length: > 0 } k
            ? $"{k.Length}:{(k.Length >= 4 ? k[^4..] : k)}"
            : "none";

        return string.Join(
            '|',
            config.ProviderType.ToString(),
            config.Model,
            s.BaseUrl ?? string.Empty,
            s.AzureDeployment ?? string.Empty,
            s.AzureApiVersion ?? string.Empty,
            s.AnthropicVersion ?? string.Empty,
            config.Enabled ? "1" : "0",
            keyFingerprint);
    }

    // Per-tenant request shaping defaults (P2b's per-tenant token budget + llm rate-limit sit
    // ABOVE the resolved provider — see spec §4 — so these are conservative classification defaults
    // mirroring LlmProviderOptions). Per-call Temperature/MaxTokens still come from the LlmRequest.
    private const double DefaultTemperature = 0.2;
    private const int DefaultMaxTokens = 800;
    private const int DefaultTimeoutSeconds = 20;

    private readonly record struct CacheEntry(string Fingerprint, ResolvedLlmProvider Resolved);
}
