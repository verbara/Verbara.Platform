using Microsoft.AspNetCore.DataProtection;

namespace Asterisk.Platform.Api.Services.AgentAssist;

/// <summary>
/// Wraps <see cref="IDataProtectionProvider"/> with the reserved purpose string
/// <c>"AgentAssist.Credentials"</c> so STT provider secrets (Deepgram / Whisper / Azure /
/// Google) can be persisted by <c>IAgentAssistFeatureToggle</c> implementations without
/// leaving plaintext in memory-dumps, logs, or (eventually) on-disk state.
/// </summary>
/// <remarks>
/// <para>
/// Design mirrors the JWT signing-key wrap shipped in Platform v1.9.2: a dedicated
/// protector per sensitive concern gives each concern independent key rotation and
/// prevents accidental cross-purpose decryption.
/// </para>
/// <para>
/// <c>Protect(dict)</c> returns a new dictionary whose values are base64-encoded ciphertext;
/// <c>Unprotect(dict)</c> reverses the transform. Keys are left untouched so lookups
/// (e.g. <c>"apiKey"</c>, <c>"endpoint"</c>) remain stable across rotations.
/// </para>
/// </remarks>
public sealed class AgentAssistCredentialsProtector
{
    /// <summary>Purpose string passed to <see cref="IDataProtectionProvider.CreateProtector(string)"/>.</summary>
    public const string Purpose = "AgentAssist.Credentials";

    private readonly IDataProtector _protector;

    public AgentAssistCredentialsProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose);
    }

    /// <summary>Encrypts every value in <paramref name="credentials"/>; keys are preserved verbatim.</summary>
    public IReadOnlyDictionary<string, string> Protect(IReadOnlyDictionary<string, string> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var result = new Dictionary<string, string>(credentials.Count, StringComparer.Ordinal);
        foreach (var (key, value) in credentials)
        {
            result[key] = _protector.Protect(value);
        }
        return result;
    }

    /// <summary>Decrypts every value in <paramref name="credentials"/>; keys are preserved verbatim.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Thrown when ciphertext is invalid.</exception>
    public IReadOnlyDictionary<string, string> Unprotect(IReadOnlyDictionary<string, string> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var result = new Dictionary<string, string>(credentials.Count, StringComparer.Ordinal);
        foreach (var (key, value) in credentials)
        {
            result[key] = _protector.Unprotect(value);
        }
        return result;
    }
}
