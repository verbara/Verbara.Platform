using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

/// <summary>
/// The merge rule of a channel configuration <c>PUT</c> (whatsapp-works-for-real D15): applied key by key to
/// the stored credentials, an omitted key or a blank value keeps the stored value, a non-blank value replaces
/// it, and a <see langword="null"/> value removes the key. A blank value never erases a stored secret, so a
/// console that leaves a secret field empty keeps the secret.
/// </summary>
public static class ChannelCredentialMerge
{
    /// <summary>The merged credentials and the names of the keys whose stored value changed.</summary>
    /// <param name="Credentials">What to store.</param>
    /// <param name="ChangedKeys">Keys replaced with a different value or removed, in ordinal order. Names only.</param>
    public sealed record Result(IReadOnlyDictionary<string, string> Credentials, IReadOnlyList<string> ChangedKeys);

    /// <summary>
    /// The keys of <paramref name="requested"/> that <paramref name="channel"/>'s declared schema does not
    /// contain, in ordinal order; always empty for a channel without a declared schema.
    /// </summary>
    public static IReadOnlyList<string> UnknownKeys(ChannelType channel, IReadOnlyDictionary<string, string?>? requested)
    {
        if (requested is null || ChannelCredentialSchemas.For(channel) is not { } schema)
            return [];

        return requested.Keys.Where(k => !schema.Contains(k)).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Applies <paramref name="requested"/> to <paramref name="stored"/> (either may be null).</summary>
    public static Result Apply(IReadOnlyDictionary<string, string>? stored, IReadOnlyDictionary<string, string?>? requested)
    {
        var merged = stored is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(stored, StringComparer.Ordinal);
        var changed = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (key, value) in requested ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                if (merged.Remove(key))
                    changed.Add(key);
            }
            else if (!string.IsNullOrWhiteSpace(value))
            {
                if (!merged.TryGetValue(key, out var current) || !string.Equals(current, value, StringComparison.Ordinal))
                    changed.Add(key);
                merged[key] = value;
            }
            // Blank: keep whatever is stored (or nothing).
        }

        return new Result(merged, changed.ToList());
    }
}
