using System.Text.Json.Serialization;

namespace Verbara.Platform.Llm;

/// <summary>
/// Ownership discriminator for a tenant's Typification LLM provider — distinct
/// from <see cref="ProviderType"/> (the provider *family*). <c>Byo</c> uses the
/// tenant's own encrypted key; <c>PlatformManaged</c> uses Verbara's operator
/// key (host-bound <c>PlatformLlmOptions</c>), metered + billed in AI Credits.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AiSource>))]
public enum AiSource
{
    Byo = 0,
    PlatformManaged = 1,
}
