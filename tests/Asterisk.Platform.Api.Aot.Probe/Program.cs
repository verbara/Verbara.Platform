// Asterisk.Platform.Api.Aot.Probe — AHH Phase 0 AOT validation.
//
// This probe exercises every Argon2id call site we plan to hit in Phase 4
// (verify a known hash, hash a password with OWASP-2025 floor params) and
// publishes under PublishAot=true. The publish must complete with ZERO
// trim/AOT warnings; any IL2026 / IL2070 / IL2104 / IL3050 etc. fails the
// gate and pivots Phase 4 to the libsodium P/Invoke fallback.
//
// Repro:
//   dotnet publish -c Release -p:PublishAot=true \
//     tests/Asterisk.Platform.Api.Aot.Probe/Asterisk.Platform.Api.Aot.Probe.csproj
//
// Capture the publish log and paste the warning count + native-image size into
// docs/research/2026-04-XX-auth-hotpath-baseline.md.

using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;

const string password = "correct horse battery staple";

// 1. Hash with OWASP-2025 floor params (m=19 MiB, t=2, p=1, hash length 32).
var passwordBytes = Encoding.UTF8.GetBytes(password);
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

string encoded;
using (var argon2 = new Argon2(config))
using (var hash = argon2.Hash())
{
    encoded = config.EncodeString(hash.Buffer);
}

// 2. Verify path — same surface Phase 4's CompositePasswordHasher.Verify hits.
var ok = Argon2.Verify(encoded, password);
if (!ok)
{
    Console.Error.WriteLine("AOT probe FAILED: Argon2.Verify returned false on a freshly hashed password.");
    return 1;
}

Console.WriteLine($"AOT probe OK. Encoded length={encoded.Length}, hash bytes={32}.");
return 0;
