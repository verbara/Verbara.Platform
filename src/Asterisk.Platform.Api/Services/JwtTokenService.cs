using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Asterisk.Platform.Identity;
using Microsoft.IdentityModel.Tokens;

namespace Asterisk.Platform.Api.Services;

internal sealed class JwtTokenService
{
    private const string Issuer = "asterisk-platform";
    private const string Audience = "asterisk-platform";
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    private readonly RsaSecurityKey _signingKey;
    private readonly SigningCredentials _signingCredentials;
    private readonly TokenValidationParameters _validationParameters;

    public JwtTokenService(string dataDirectory)
    {
        var keyPath = Path.Combine(dataDirectory, "jwt-signing-key.xml");
        var rsa = RSA.Create(2048);

        if (File.Exists(keyPath))
            rsa.FromXmlString(File.ReadAllText(keyPath));
        else
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(keyPath, rsa.ToXmlString(includePrivateParameters: true));
        }

        _signingKey = new RsaSecurityKey(rsa) { KeyId = "platform-jwt-key-1" };
        _signingCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _signingKey,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    }

    public TokenValidationParameters ValidationParameters => _validationParameters;

    public (string Token, DateTimeOffset ExpiresAt) GenerateAccessToken(User user)
        => GenerateAccessToken(user, null);

    public (string Token, DateTimeOffset ExpiresAt) GenerateAccessToken(User user, IReadOnlySet<string>? permissions)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(AccessTokenLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId.Value),
            new("tid", user.TenantId.Value),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
        };

        // Include granular permissions in the JWT when available
        if (permissions is { Count: > 0 })
        {
            foreach (var permission in permissions)
            {
                claims.Add(new Claim("permissions", permission));
            }
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Issuer = Issuer,
            Audience = Audience,
            SigningCredentials = _signingCredentials,
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateEncodedJwt(descriptor);
        return (token, expiresAt);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, _validationParameters, out _);
            return principal;
        }
        catch { return null; }
    }
}
