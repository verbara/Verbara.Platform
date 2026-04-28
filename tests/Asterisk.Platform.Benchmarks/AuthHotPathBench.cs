// Asterisk.Platform.Benchmarks — AHH Phase 0 evidence benchmarks.
//
// Goal: empirically attribute the per-request cost of POST /auth/login on
// the AMD 9900X / 60 GB / docker-compose target. R5.5 measured a 75 req/s
// knee; the benches here isolate the dominant operation (BCrypt12 verify,
// expected ~75 ms) from the rest of the path (RSA sign, expected <1 ms).
//
// They also project the post-Phase-4 ceiling by measuring Argon2id at the
// OWASP-2025 params (m=19 MiB, t=2, p=1) — the planned replacement.
//
// Repro: dotnet run -c Release --project tests/Asterisk.Platform.Benchmarks
//        -- --filter '*AuthHotPathBench*'

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Isopoh.Cryptography.Argon2;
using Microsoft.IdentityModel.Tokens;

namespace Asterisk.Platform.Benchmarks;

[MemoryDiagnoser]
public class AuthHotPathBench
{
    // Same plaintext for every iteration — we measure verify cost, not entropy.
    private const string Password = "correct horse battery staple";

    private string _bcrypt12Hash = string.Empty;
    private string _argon2idHash = string.Empty;
    private SigningCredentials _signingCredentials = null!;
    private SecurityTokenDescriptor _jwtDescriptor = null!;
    private JwtSecurityTokenHandler _jwtHandler = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Pre-hash both algorithms so verify benches start with a known artifact.
        _bcrypt12Hash = BCrypt.Net.BCrypt.HashPassword(Password, workFactor: 12);
        _argon2idHash = HashArgon2idOwasp2025(Password);

        // Mirror JwtTokenService.cs: cache the RSA key + signing credentials at
        // setup so the per-iteration JWT bench reflects the real hot path.
        var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "phase0-bench" };
        _signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
        _jwtHandler = new JwtSecurityTokenHandler();

        var now = DateTime.UtcNow;
        _jwtDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, "user-123"),
                new Claim("tid", "tenant-abc"),
                new Claim(JwtRegisteredClaimNames.Email, "user@example.com"),
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim(JwtRegisteredClaimNames.Jti, "11111111-1111-1111-1111-111111111111"),
            ]),
            Expires = now.AddMinutes(15),
            IssuedAt = now,
            Issuer = "asterisk-platform",
            Audience = "asterisk-platform",
            SigningCredentials = _signingCredentials,
        };
    }

    /// <summary>
    /// Current production password verify path. Expected ~75 ms on AMD 9900X
    /// (workFactor=12). This is the cost at the documented R5.5 knee.
    /// </summary>
    [Benchmark(Baseline = true)]
    public bool Bcrypt12_Verify()
        => BCrypt.Net.BCrypt.Verify(Password, _bcrypt12Hash);

    /// <summary>
    /// Phase-4 post-migration verify path with OWASP-2025 floor params
    /// (m=19 MiB, t=2, p=1). Expected ~25–35 ms on AMD 9900X. Acceptance:
    /// must be ≤ 40 ms p99.
    /// </summary>
    [Benchmark]
    public bool Argon2id_Verify_OwaspParams()
        => Argon2.Verify(_argon2idHash, Password);

    /// <summary>
    /// JWT issuance with cached RSA-2048 signing credentials. Mirrors the
    /// per-request work in <c>JwtTokenService.GenerateAccessToken</c>.
    /// Expected &lt; 1 ms — confirming JWT signing is NOT the bottleneck.
    /// </summary>
    [Benchmark]
    public string JwtRsaSign_Issue()
        => _jwtHandler.CreateEncodedJwt(_jwtDescriptor);

    /// <summary>
    /// Composite of the two operations on the synchronous request path that
    /// matter for the knee: BCrypt verify + RSA-2048 JWT sign. Excludes
    /// DB I/O (those are micro-bench-irrelevant in this dimension; the real
    /// DB cost is measured in the docker-compose load tests).
    /// </summary>
    [Benchmark]
    public string EndToEnd_BcryptThenJwtSign()
    {
        var ok = BCrypt.Net.BCrypt.Verify(Password, _bcrypt12Hash);
        return ok ? _jwtHandler.CreateEncodedJwt(_jwtDescriptor) : string.Empty;
    }

    /// <summary>
    /// Projected post-Phase-4 composite: Argon2id verify + RSA-2048 JWT sign.
    /// Used to estimate the post-migration single-request floor.
    /// </summary>
    [Benchmark]
    public string EndToEnd_Argon2idThenJwtSign()
    {
        var ok = Argon2.Verify(_argon2idHash, Password);
        return ok ? _jwtHandler.CreateEncodedJwt(_jwtDescriptor) : string.Empty;
    }

    private static string HashArgon2idOwasp2025(string password)
    {
        // OWASP 2025 floor for Argon2id: m=19 MiB, t=2, p=1.
        // MemoryCost is in KiB → 19 * 1024 = 19456.
        var passwordBytes = System.Text.Encoding.UTF8.GetBytes(password);
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            TimeCost = 2,
            MemoryCost = 19456,
            Lanes = 1,
            Threads = 1,
            Password = passwordBytes,
            Salt = salt,
            HashLength = 32,
        };

        using var argon2 = new Argon2(config);
        using var hash = argon2.Hash();
        return config.EncodeString(hash.Buffer);
    }
}
