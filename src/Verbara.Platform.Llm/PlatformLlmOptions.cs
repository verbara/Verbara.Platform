namespace Verbara.Platform.Llm;

/// <summary>
/// Host-bound configuration for Verbara's <b>operator-managed</b> Typification
/// LLM (the provider served when a tenant sets <see cref="AiSource.PlatformManaged"/>).
/// The key lives only here — never per-tenant, never serialized to any DTO.
/// </summary>
public sealed class PlatformLlmOptions
{
    /// <summary>Operator master switch. When false, platform-managed tenants degrade to the empty suggestion.</summary>
    public bool Enabled { get; set; }

    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string Model { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 800;
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Tokens per AI Credit (commercial unit). Default 1000. Credits = Σtokens ÷ this ratio (aggregate, never per-call).</summary>
    public long CreditTokenRatio { get; set; } = 1000;
}
