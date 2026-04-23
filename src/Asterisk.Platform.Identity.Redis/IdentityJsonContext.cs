using System.Text.Json.Serialization;
using Asterisk.Platform.Identity.Mfa;

namespace Asterisk.Platform.Identity.Redis;

/// <summary>
/// AOT-safe source-generated JSON context for serializing MFA + password-reset
/// cache entries into Redis. Persisting the typed records as JSON avoids any
/// reflection-based serializer usage.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MfaPendingEntry))]
[JsonSerializable(typeof(PasswordResetEntry))]
internal sealed partial class IdentityJsonContext : JsonSerializerContext;
