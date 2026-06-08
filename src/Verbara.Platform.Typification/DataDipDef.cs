namespace Verbara.Platform.Typification;

/// <summary>
/// External lookup definition (P3 — empty in P0). Reuses the <c>http_request</c>
/// shape; executed by a <c>TypificationDataDipService</c> in a later phase.
/// </summary>
public sealed record DataDipDef
{
    /// <summary>Stable id referenced by fields.</summary>
    public required string DipId { get; init; }

    public required string Name { get; init; }

    /// <summary>GET or POST.</summary>
    public required string Method { get; init; }

    /// <summary><c>{{var}}</c>-templated URL (metadata, field values).</summary>
    public required string UrlTemplate { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public string? BodyTemplate { get; init; }

    public int TimeoutSeconds { get; init; }

    /// <summary>PII path: no plaintext logging, credentials from the secret store.</summary>
    public bool Secure { get; init; }

    public required IReadOnlyList<ResponseMapping> ResponseMappings { get; init; }
}
