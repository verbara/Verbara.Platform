using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

/// <summary>One canonical key of a channel's credential configuration.</summary>
/// <param name="Name">The key as stored in <see cref="TenantChannelConfig.Credentials"/> (ordinal).</param>
/// <param name="IsSecret">Whether the value is a secret: never returned, and wrapped at rest.</param>
public sealed record ChannelCredentialKey(string Name, bool IsSecret);

/// <summary>
/// The declared credential keys of one channel (whatsapp-works-for-real D15, spec
/// <c>channel-credential-config</c>). A channel with a schema accepts only its canonical keys as values (a
/// <see langword="null"/> may still remove any stored key); a channel without one (see
/// <see cref="ChannelCredentialSchemas.For"/>) accepts any key. Every key a schema does not declare as
/// non-secret is treated as secret, so an undeclared value is never echoed and always wrapped at rest.
/// </summary>
public sealed class ChannelCredentialSchema
{
    private readonly Dictionary<string, ChannelCredentialKey> _byName;

    public ChannelCredentialSchema(ChannelType channel, IReadOnlyList<ChannelCredentialKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Channel = channel;
        Keys = keys;
        _byName = keys.ToDictionary(k => k.Name, StringComparer.Ordinal);
    }

    public ChannelType Channel { get; }

    /// <summary>The canonical keys, in display order.</summary>
    public IReadOnlyList<ChannelCredentialKey> Keys { get; }

    /// <summary>Whether <paramref name="name"/> is one of the canonical keys (ordinal).</summary>
    public bool Contains(string name) => _byName.ContainsKey(name);

    /// <summary>Whether <paramref name="name"/> is a canonical secret key.</summary>
    public bool IsSecret(string name) => _byName.TryGetValue(name, out var key) && key.IsSecret;
}

/// <summary>
/// The credential schemas the platform declares. Lives in Channels.Core (not in each channel module) so the
/// storage layer, which references no channel module, wraps exactly the keys the API redacts.
/// </summary>
public static class ChannelCredentialSchemas
{
    /// <summary>
    /// The per-channel default queue that inbound routing reads (Tier 1 of the default-queue fallback,
    /// <c>DefaultQueueFallbackMiddleware</c>). A routing key, not a secret.
    /// </summary>
    public const string DefaultQueueIdKey = "defaultQueueId";

    /// <summary>
    /// WhatsApp (Meta Cloud API). The Meta names equal the constants of <c>WhatsAppCredentialKeys</c> — a test
    /// in the WhatsApp module pins the two together — plus the routing key <see cref="DefaultQueueIdKey"/>.
    /// </summary>
    public static ChannelCredentialSchema WhatsApp { get; } = new(
        ChannelType.WhatsApp,
        [
            new("AccessToken", IsSecret: true),
            new("PhoneNumberId", IsSecret: false),
            new("AppSecret", IsSecret: true),
            new("WebhookVerifyToken", IsSecret: true),
            new("WabaId", IsSecret: false),
            new("ApiVersion", IsSecret: false),
            new(DefaultQueueIdKey, IsSecret: false),
        ]);

    /// <summary>The declared schema of <paramref name="channel"/>, or <see langword="null"/> when it has none.</summary>
    public static ChannelCredentialSchema? For(ChannelType channel) => channel switch
    {
        ChannelType.WhatsApp => WhatsApp,
        _ => null,
    };

    /// <summary>
    /// Whether the value of <paramref name="key"/> on <paramref name="channel"/> is secret: a canonical secret
    /// key, a key outside the channel's declared schema, or any key of a channel without one. A stored key
    /// outside the schema may hold a real secret — before Web #393 the console saved the WhatsApp token as
    /// <c>ApiToken</c> — so only a key the schema declares non-secret is stored and shown as is.
    /// </summary>
    public static bool IsSecret(ChannelType channel, string key) =>
        For(channel) is not { } schema || !schema.Contains(key) || schema.IsSecret(key);
}
