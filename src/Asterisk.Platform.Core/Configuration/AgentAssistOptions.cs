namespace Asterisk.Platform.Core.Configuration;

/// <summary>
/// Seed configuration for the AgentAssist runtime feature toggle. Bound from the
/// <c>"AgentAssist"</c> configuration section at startup; the resulting snapshot
/// is handed to <c>InMemoryAgentAssistFeatureToggle</c> which owns the live state.
/// Subsequent runtime changes via the admin endpoint update the in-memory toggle
/// only — they do NOT round-trip back to <c>appsettings.json</c>.
/// </summary>
public sealed class AgentAssistOptions
{
    /// <summary>Master on/off switch; when <c>false</c>, the engine skips transcription at session start.</summary>
    public bool Enabled { get; set; }

    /// <summary>Lowercase provider id: <c>"deepgram"</c> | <c>"whisper"</c> | <c>"azure-whisper"</c> | <c>"google"</c>.</summary>
    public string? Provider { get; set; }

    /// <summary>Deepgram WebSocket streaming credentials.</summary>
    public AgentAssistProviderCredentials Deepgram { get; set; } = new();

    /// <summary>OpenAI Whisper REST credentials.</summary>
    public AgentAssistProviderCredentials Whisper { get; set; } = new();

    /// <summary>Azure Cognitive Services Whisper credentials (Endpoint + ApiKey).</summary>
    public AgentAssistProviderCredentials Azure { get; set; } = new();

    /// <summary>Google Cloud Speech-to-Text credentials (ApiKey holds service-account JSON or API key).</summary>
    public AgentAssistProviderCredentials Google { get; set; } = new();
}

/// <summary>Per-provider credential material. Plain strings here are NEVER persisted; they are
/// always encrypted via <c>IDataProtectionProvider</c> before being handed to the feature toggle.</summary>
public sealed class AgentAssistProviderCredentials
{
    /// <summary>API key / secret. For Google this field holds the service-account JSON text.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional provider-specific endpoint (required for Azure, optional for Whisper compatible servers).</summary>
    public string? Endpoint { get; set; }
}
