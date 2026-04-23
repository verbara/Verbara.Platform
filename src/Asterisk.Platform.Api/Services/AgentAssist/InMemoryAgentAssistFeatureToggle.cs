using Asterisk.Platform.Core.Configuration;
using Asterisk.Sdk.Pro.AgentAssist.Features;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Services.AgentAssist;

/// <summary>
/// Process-scoped in-memory <see cref="IAgentAssistFeatureToggle"/>. Seeds its state from
/// <see cref="AgentAssistOptions"/> (bound from appsettings) the first time the toggle is read,
/// then keeps the current state in a guarded field. Admin endpoint updates mutate this field
/// atomically; credential bytes are ALWAYS already encrypted by
/// <see cref="AgentAssistCredentialsProtector"/> before they land here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Known limitation (Phase 1 / R5.1 Task J):</b> the toggle state lives on each API node.
/// Multi-instance deployments therefore see per-node state divergence until the next restart
/// (when config-file <see cref="AgentAssistOptions"/> re-seeds both nodes to the same baseline).
/// A Postgres-backed toggle is tracked for R5.2 / follow-up.
/// </para>
/// <para>
/// Parallels the pause-tracker in-memory fallback shipped in R5.1 Task I.
/// </para>
/// </remarks>
public sealed class InMemoryAgentAssistFeatureToggle : IAgentAssistFeatureToggle
{
    private readonly AgentAssistOptions _seedOptions;
    private readonly AgentAssistCredentialsProtector _protector;
    private readonly Lock _lock = new();
    private AgentAssistFeatureState _current;
    private bool _seeded;

    public InMemoryAgentAssistFeatureToggle(
        IOptions<AgentAssistOptions> options,
        AgentAssistCredentialsProtector protector)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protector);
        _seedOptions = options.Value;
        _protector = protector;
        _current = AgentAssistFeatureState.Disabled; // replaced on first read via SeedIfNeeded()
    }

    public ValueTask<AgentAssistFeatureState> GetCurrentAsync(CancellationToken ct = default)
    {
        SeedIfNeeded();
        lock (_lock)
        {
            return ValueTask.FromResult(_current);
        }
    }

    public ValueTask<AgentAssistFeatureState> SetAsync(AgentAssistFeatureState desired, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(desired);
        SeedIfNeeded();
        lock (_lock)
        {
            _current = desired;
            return ValueTask.FromResult(_current);
        }
    }

    private void SeedIfNeeded()
    {
        if (_seeded) return;
        lock (_lock)
        {
            if (_seeded) return;

            if (!_seedOptions.Enabled || string.IsNullOrWhiteSpace(_seedOptions.Provider))
            {
                _current = AgentAssistFeatureState.Disabled;
            }
            else
            {
                var plain = ExtractCredentials(_seedOptions);
                var encrypted = plain.Count > 0 ? _protector.Protect(plain) : null;
                _current = new AgentAssistFeatureState(
                    Enabled: true,
                    Provider: _seedOptions.Provider!.Trim().ToLowerInvariant(),
                    ProtectedCredentials: encrypted,
                    UpdatedAt: null,
                    UpdatedBy: "appsettings");
            }

            _seeded = true;
        }
    }

    private static Dictionary<string, string> ExtractCredentials(AgentAssistOptions opts)
    {
        var creds = new Dictionary<string, string>(StringComparer.Ordinal);
        var provider = opts.Provider?.Trim().ToLowerInvariant();
        AgentAssistProviderCredentials? src = provider switch
        {
            "deepgram" => opts.Deepgram,
            "whisper" => opts.Whisper,
            "azure-whisper" => opts.Azure,
            "google" => opts.Google,
            _ => null,
        };
        if (src is null) return creds;

        if (!string.IsNullOrWhiteSpace(src.ApiKey))
            creds["apiKey"] = src.ApiKey!;
        if (!string.IsNullOrWhiteSpace(src.Endpoint))
            creds["endpoint"] = src.Endpoint!;
        return creds;
    }
}
