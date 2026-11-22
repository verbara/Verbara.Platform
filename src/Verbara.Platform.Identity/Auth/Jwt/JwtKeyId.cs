using System.Buffers.Text;
using System.Text.Json;

namespace Verbara.Platform.Identity.Auth.Jwt;

/// <summary>
/// Reads the <c>kid</c> a token's header names, before the token is checked, so the validation keys
/// can be brought up to date for it (<see cref="JwtValidationKeySnapshot.EnsureLoadedAsync(string?, CancellationToken)"/>).
/// Nothing here is trusted: the signature check that follows decides.
/// </summary>
public static class JwtKeyId
{
    // A real header is well under 1 KB; anything longer is not worth decoding.
    private const int MaxEncodedHeaderLength = 2048;

    /// <summary>
    /// The key id of the token the JwtBearer handler will check: <paramref name="token"/> when set
    /// (a query-string token), otherwise the one in a <c>Bearer</c> <paramref name="authorization"/>
    /// header. <see langword="null"/> when there is none or it cannot be read.
    /// </summary>
    public static string? FromRequest(string? token, string? authorization)
    {
        if (string.IsNullOrEmpty(token)
            && authorization is not null
            && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = authorization["Bearer ".Length..].Trim();
        }
        return FromToken(token);
    }

    /// <summary>The <c>kid</c> in a compact JWT's header, or <see langword="null"/>.</summary>
    public static string? FromToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot > MaxEncodedHeaderLength)
            return null;

        try
        {
            var header = Base64Url.DecodeFromChars(token.AsSpan(0, dot));
            var reader = new Utf8JsonReader(header);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isKid = reader.ValueTextEquals("kid"u8);
                if (!reader.Read())
                    return null;
                if (isKid)
                    return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                reader.Skip();
            }
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
