namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// A tenant's allow-list policy for storing personally-identifiable information that the
/// AI may extract into typification field values (D1, P2b). Any <see cref="PiiType"/>
/// NOT in <see cref="AllowStore"/> is masked by <see cref="PiiScreen.Apply"/>; allow-listed
/// types are passed through unchanged.
/// </summary>
public sealed class PiiPolicy
{
    /// <summary>
    /// PII types the tenant has explicitly allowed to store unmasked. Empty (the default)
    /// = deny all sensitive types (mask everything detected).
    /// </summary>
    public required IReadOnlySet<PiiType> AllowStore { get; init; }

    /// <summary>The most restrictive policy: every detected PII type is masked.</summary>
    public static PiiPolicy DenyAll { get; } = new() { AllowStore = new HashSet<PiiType>() };
}
