using System.Text.Json.Serialization;
using Verbara.Platform.Identity.Auth.Jwt;
using Verbara.Platform.Identity.Mfa;

namespace Verbara.Platform.Identity.Redis;

/// <summary>
/// AOT-safe source-generated JSON context for serializing MFA + password-reset
/// cache entries + JWT key rotation entries into Redis. Persisting the typed
/// records as JSON avoids any reflection-based serializer usage.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MfaPendingEntry))]
[JsonSerializable(typeof(PasswordResetEntry))]
// R5.4 S5.9 — JWT signing-key rotation entries persisted by RedisJwtKeyStore.
[JsonSerializable(typeof(JwtKeyEntry))]
internal sealed partial class IdentityJsonContext : JsonSerializerContext;
