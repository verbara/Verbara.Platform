using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly PlatformLlmOptions _platform;

    // tenantId.Value → (fingerprint, resolved). One entry per tenant; Invalidate evicts by tenant.
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public DefaultLlmProviderResolver(
        ITenantLlmConfigStore store,
        IHttpClientFactory httpFactory,
        IServiceProvider serviceProvider,
        IMeterFactory? meterFactory = null,
        ILoggerFactory? loggerFactory = null,
        IOptions<PlatformLlmOptions>? platformOptions = null)
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
        _platform = platformOptions?.Value ?? new PlatformLlmOptions();
    }

    /// <inheritdoc />
    public async Task<ResolvedLlmProvider?> ResolveAsync(EntityId tenantId, CancellationToken ct)
    {
        var config = await _store.GetAsync(tenantId, ct).ConfigureAwait(false);
        var unusable = config is null
            || !config.Enabled
            || (config.AiSource == AiSource.PlatformManaged
                    ? !_platform.Enabled                       // platform: operator switch
                    : string.IsNullOrEmpty(config!.ApiKey));   // BYO: tenant key required
        if (unusable)
        {
            // Fail-closed: no config / disabled, plus a BYO config with no key OR a platform-managed
            // config with the operator switch off, are all valid "AI off" states — a keyless provider
            // would throw at call time, so treat it as cleanly off. Drop any stale entry (disposing
            // its provider).
            if (_cache.TryRemove(tenantId.Value, out var stale))
                (stale.Resolved.Provider as IDisposable)?.Dispose();
            return null;
        }

        // The `unusable` guard above already returned on `config is null`.
        var fingerprint = ComputeFingerprint(config!);

        if (_cache.TryGetValue(tenantId.Value, out var cached) && cached.Fingerprint == fingerprint)
            return cached.Resolved;

        var resolved = Build(config!);

        // Replace the entry; dispose the outgoing provider (fingerprint change) so its Meter/handler
        // resources don't leak.
        if (_cache.TryGetValue(tenantId.Value, out var previous) && previous.Fingerprint != fingerprint)
            (previous.Resolved.Provider as IDisposable)?.Dispose();
        _cache[tenantId.Value] = new CacheEntry(fingerprint, resolved);
        return resolved;
    }

    /// <inheritdoc />
    public void Invalidate(EntityId tenantId)
    {
        // Dispose the outgoing provider on eviction so its Meter/HTTP-handler resources don't leak.
        if (_cache.TryRemove(tenantId.Value, out var removed))
            (removed.Resolved.Provider as IDisposable)?.Dispose();
    }

    // Production resolve path: share the keyed `llm.completions` circuit + retry policy across tenants.
    private ResolvedLlmProvider Build(TenantLlmConfig config) =>
        new(BuildWithPolicy(config, _policy),
            config.AiSource == AiSource.PlatformManaged ? _platform.Model : config.Model);

    /// <inheritdoc />
    public ILlmProvider BuildTransient(TenantLlmConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // /test probes use an isolated NO-OP policy so a failing probe (a typo'd endpoint, a bad key)
        // never trips the SHARED production `llm.completions` circuit breaker that every tenant's
        // real classify calls flow through.
        return BuildWithPolicy(config, ResiliencePolicy.NoOp);
    }

    private ILlmProvider BuildWithPolicy(TenantLlmConfig config, ResiliencePolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.AiSource == AiSource.PlatformManaged)
        {
            var platformEffective = new LlmEffectiveOptions(
                BaseUrl:        _platform.BaseUrl,
                ApiKey:         _platform.ApiKey,
                Model:          _platform.Model,
                Temperature:    _platform.Temperature,
                MaxTokens:      _platform.MaxTokens,
                TimeoutSeconds: _platform.TimeoutSeconds);
            return new OpenAiCompatibleLlmProvider(
                _httpFactory.CreateClient(OpenAiClientName),
                platformEffective,
                policy, _meterFactory,
                _loggerFactory?.CreateLogger<OpenAiCompatibleLlmProvider>());
        }

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
                policy,
                _meterFactory,
                _loggerFactory?.CreateLogger<AzureOpenAiLlmProvider>()),

            ProviderType.Anthropic => new AnthropicLlmProvider(
                _httpFactory.CreateClient(AnthropicClientName),
                effective,
                config.Settings.AnthropicVersion,
                policy,
                _meterFactory,
                _loggerFactory?.CreateLogger<AnthropicLlmProvider>()),

            _ => new OpenAiCompatibleLlmProvider(
                _httpFactory.CreateClient(OpenAiClientName),
                effective,
                policy,
                _meterFactory,
                _loggerFactory?.CreateLogger<OpenAiCompatibleLlmProvider>()),
        };
    }

    /// <summary>
    /// A stable fingerprint of the persisted config — provider type, model, settings, enabled flag,
    /// and a key fingerprint. The key fingerprint folds in <see cref="TenantLlmConfig.UpdatedAt"/>
    /// (bumped on every upsert) so a key ROTATION that keeps the same length + last-4 still yields a
    /// new fingerprint and rebuilds the provider. The raw key is NEVER logged or persisted here.
    /// </summary>
    private string ComputeFingerprint(TenantLlmConfig config)
    {
        var s = config.Settings;
        var keyFingerprint = config.ApiKey is { Length: > 0 } k
            ? $"{k.Length}:{(k.Length >= 4 ? k[^4..] : k)}:{config.UpdatedAt.UtcTicks}"
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
            keyFingerprint,
            config.AiSource.ToString(),
            config.AiSource == AiSource.PlatformManaged
                ? $"plat:{_platform.Enabled}:{_platform.Model}:{_platform.BaseUrl}:{(_platform.ApiKey is { Length: >= 4 } pk ? pk[^4..] : "none")}"
                : "byo");
    }

    // Per-tenant request shaping defaults (P2b's per-tenant token budget + llm rate-limit sit
    // ABOVE the resolved provider — see spec §4 — so these are conservative classification defaults
    // mirroring LlmProviderOptions). Per-call Temperature/MaxTokens still come from the LlmRequest.
    private const double DefaultTemperature = 0.2;
    private const int DefaultMaxTokens = 800;
    private const int DefaultTimeoutSeconds = 20;

    private readonly record struct CacheEntry(string Fingerprint, ResolvedLlmProvider Resolved);
}
