using System.Globalization;
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
    /// The keys of <paramref name="requested"/> that carry a value although <paramref name="channel"/>'s
    /// declared schema does not contain them, in ordinal order; always empty for a channel without a declared
    /// schema. A <see langword="null"/> entry is never unknown: it removes the key, so an administrator can
    /// delete a stored legacy key that is outside the schema (e.g. the pre-Web #393 <c>ApiToken</c>).
    /// </summary>
    public static IReadOnlyList<string> UnknownKeys(ChannelType channel, IReadOnlyDictionary<string, string?>? requested)
    {
        if (requested is null || ChannelCredentialSchemas.For(channel) is not { } schema)
            return [];

        return requested
            .Where(kv => kv.Value is not null && !schema.Contains(kv.Key))
            .Select(kv => kv.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The shortest WebChat identity token secret an administrator can set.</summary>
    public const int MinIdentityTokenSecretLength = 32;

    /// <summary>The longest WebChat identity token secret an administrator can set.</summary>
    public const int MaxIdentityTokenSecretLength = 512;

    /// <summary>
    /// The keys of <paramref name="requested"/> only the server may write, in ordinal order: a value for the previous
    /// WebChat secret (a <see langword="null"/> is the revoke, and a blank keeps what is stored, as for every key), and
    /// the rotation stamp whenever it is named at all, because only a rotation sets it and only a revoke clears it.
    /// </summary>
    public static IReadOnlyList<string> ServerManagedKeys(ChannelType channel, IReadOnlyDictionary<string, string?>? requested)
    {
        if (requested is null || channel != ChannelType.WebChat)
            return [];

        var keys = new List<string>();
        if (requested.TryGetValue(ChannelCredentialSchemas.IdentityTokenSecretPreviousKey, out var previous)
            && !string.IsNullOrWhiteSpace(previous))
        {
            keys.Add(ChannelCredentialSchemas.IdentityTokenSecretPreviousKey);
        }

        if (requested.ContainsKey(ChannelCredentialSchemas.IdentityTokenSecretReplacedAtKey))
            keys.Add(ChannelCredentialSchemas.IdentityTokenSecretReplacedAtKey);

        return keys;
    }

    /// <summary>The WebChat secret of <paramref name="requested"/> when its value is outside the allowed length.</summary>
    public static IReadOnlyList<string> WeakSecretKeys(ChannelType channel, IReadOnlyDictionary<string, string?>? requested)
    {
        if (requested is null
            || channel != ChannelType.WebChat
            || !requested.TryGetValue(ChannelCredentialSchemas.IdentityTokenSecretKey, out var secret)
            || string.IsNullOrWhiteSpace(secret))
        {
            return [];
        }

        return secret.Length is < MinIdentityTokenSecretLength or > MaxIdentityTokenSecretLength
            ? [ChannelCredentialSchemas.IdentityTokenSecretKey]
            : [];
    }

    /// <summary>
    /// Applies <paramref name="requested"/> to <paramref name="stored"/> for <paramref name="channel"/>. On WebChat,
    /// replacing the identity token secret also keeps the old one as the previous secret, stamped with
    /// <paramref name="now"/>, so a site has a grace to deploy the new one. Removing the current or the previous secret
    /// ends that grace: a previous secret never outlives its stamp or verifies on its own, so a request that replaces
    /// the secret and removes the previous one keeps no grace at all.
    /// </summary>
    public static Result Apply(
        ChannelType channel,
        IReadOnlyDictionary<string, string>? stored,
        IReadOnlyDictionary<string, string?>? requested,
        DateTimeOffset now)
    {
        var plain = Apply(stored, requested);
        if (channel != ChannelType.WebChat || requested is null)
            return plain;

        const string Current = ChannelCredentialSchemas.IdentityTokenSecretKey;
        const string Previous = ChannelCredentialSchemas.IdentityTokenSecretPreviousKey;
        const string ReplacedAt = ChannelCredentialSchemas.IdentityTokenSecretReplacedAtKey;

        var merged = new Dictionary<string, string>(plain.Credentials, StringComparer.Ordinal);
        var removed = (requested.TryGetValue(Current, out var current) && current is null)
                      || (requested.TryGetValue(Previous, out var previous) && previous is null);
        if (removed)
        {
            merged.Remove(Previous);
            merged.Remove(ReplacedAt);
        }
        else if (stored is not null
                 && stored.TryGetValue(Current, out var replaced)
                 && !string.IsNullOrWhiteSpace(replaced)
                 && !string.IsNullOrWhiteSpace(current)
                 && !string.Equals(replaced, current, StringComparison.Ordinal))
        {
            merged[Previous] = replaced;
            merged[ReplacedAt] = now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        }

        var changed = new SortedSet<string>(plain.ChangedKeys, StringComparer.Ordinal);
        foreach (var key in new[] { Previous, ReplacedAt })
        {
            string? before = null;
            stored?.TryGetValue(key, out before);
            merged.TryGetValue(key, out var after);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                changed.Add(key);
        }

        return new Result(merged, changed.ToList());
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
