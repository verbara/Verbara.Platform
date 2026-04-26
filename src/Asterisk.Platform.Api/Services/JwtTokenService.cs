using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Api.Auth;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Identity.Auth;
using Microsoft.AspNetCore.DataProtection;
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
    private readonly IJtiRevocationCache _revocationCache;

    public JwtTokenService(string dataDirectory, IDataProtectionProvider dataProtection, IJtiRevocationCache revocationCache)
    {
        _revocationCache = revocationCache;

        var keyPath = Path.Combine(dataDirectory, "jwt-signing-key.xml");
        var rsa = RSA.Create(2048);
        var protector = dataProtection.CreateProtector("Asterisk.Platform.Jwt.SigningKey");

        if (File.Exists(keyPath))
        {
            var raw = File.ReadAllBytes(keyPath);
            try
            {
                // Try to unprotect (encrypted format written by v1.9.2+)
                var xmlString = Encoding.UTF8.GetString(protector.Unprotect(raw));
                rsa.FromXmlString(xmlString);
            }
            catch (CryptographicException)
            {
                // Legacy plaintext XML — migrate silently on next restart
                MigrateLegacyKey(rsa, raw, keyPath, protector);
            }
            catch (FormatException)
            {
                // Corrupt base64 or similar — treat as plaintext
                MigrateLegacyKey(rsa, raw, keyPath, protector);
            }
        }
        else
        {
            Directory.CreateDirectory(dataDirectory);
            var xmlString = rsa.ToXmlString(includePrivateParameters: true);
            File.WriteAllBytes(keyPath, protector.Protect(Encoding.UTF8.GetBytes(xmlString)));
        }

        // Derive kid from public key fingerprint (stable per key, changes on rotation)
        var publicParams = rsa.ExportParameters(false);
        var modulusHash = SHA256.HashData(publicParams.Modulus!);
        var kid = $"platform-jwt-{Convert.ToHexStringLower(modulusHash.AsSpan(0, 8))}";

        _signingKey = new RsaSecurityKey(rsa) { KeyId = kid };
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
            RoleClaimType = "role",
            NameClaimType = "sub",
        };
    }

    public TokenValidationParameters ValidationParameters => _validationParameters;

    public string KeyId => _signingKey.KeyId;

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
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
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

    public (string Token, DateTimeOffset ExpiresAt) GenerateImpersonationToken(
        User admin, string targetTenantId, IReadOnlySet<string> targetPermissions, bool readOnly = false)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(TimeSpan.FromMinutes(30));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, admin.UserId.Value),
            new("tid", targetTenantId),
            new(JwtRegisteredClaimNames.Email, admin.Email),
            new("name", admin.DisplayName),
            new(ClaimTypes.Role, "Admin"),
            new("impersonator_id", admin.UserId.Value),
            new("impersonator_tenant", admin.TenantId.Value),
            new("impersonation", "true"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        if (readOnly)
            claims.Add(new Claim("readonly", "true"));

        foreach (var permission in targetPermissions)
        {
            claims.Add(new Claim("permissions", permission));
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

    public async ValueTask<ClaimsPrincipal?> ValidateTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, _validationParameters, out _);

            // Check jti revocation
            var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
            if (jti is not null && await _revocationCache.IsRevokedAsync(jti, ct))
                return null;

            return principal;
        }
        catch { return null; }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static void MigrateLegacyKey(RSA rsa, byte[] raw, string keyPath, IDataProtector protector)
    {
        var xmlString = Encoding.UTF8.GetString(raw);
        rsa.FromXmlString(xmlString);
        // Re-write encrypted immediately — silently migrates existing deployments
        File.WriteAllBytes(keyPath, protector.Protect(Encoding.UTF8.GetBytes(xmlString)));
    }
}
