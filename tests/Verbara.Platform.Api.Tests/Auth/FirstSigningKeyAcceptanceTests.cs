using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth.Jwt;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// The real Api host on the rotation pool, through its own JwtBearer <c>OnMessageReceived</c> event: a
/// replica that read the pool while it was empty (a fresh install before setup) accepts the first
/// token signed with the first key another replica created, on the first request.
/// </summary>
public sealed class FirstSigningKeyAcceptanceTests
{
    [Fact]
    public async Task BearerRequest_ShouldBeAcceptedOnTheFirstRequest_WhenTheFirstSigningKeyAppearsAfterThePoolWasReadEmpty()
    {
        using var host = new RotationPoolApiFactory();
        (await host.Services.GetRequiredService<IJwtKeyStore>().GetAllAsync())
            .Should().BeEmpty(because: "a fresh install holds no signing key before setup");

        // Before setup, requests reach the replica (probes, the setup screen): its keys load, empty.
        // This one carries a token that names no key, so the event loads the pool and goes no further.
        (await GetUsersAsync(host, SignedToken(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)))))
            .Should().Be(HttpStatusCode.Unauthorized);

        // Setup creates the first key in the pool: through the rotation service, as the replica that
        // runs setup does, so nothing adds the key to this replica's validation keys.
        var first = await host.Services.GetRequiredService<IJwtKeyRotationService>().GetActiveSigningKeyAsync();
        first.Algorithm.Should().Be(JwtKeyAlgorithm.Hs256, because: "the pool bootstraps an HS256 key");
        var token = SignedToken(new SymmetricSecurityKey(Convert.FromBase64String(first.Key)) { KeyId = first.KeyId });

        (await GetUsersAsync(host, token)).Should().Be(HttpStatusCode.OK,
            because: "the admin's first request after setup must be accepted, not refused until a backoff ends");
    }

    private static async Task<HttpStatusCode> GetUsersAsync(RotationPoolApiFactory host, string bearer)
    {
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await client.GetAsync(new Uri("/api/v1/admin/users", UriKind.Relative));
        return response.StatusCode;
    }

    private static string SignedToken(SymmetricSecurityKey key)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "verbara-platform",
            Audience = "verbara-platform",
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", AccountStatusApiFactory.CustomerAdminUserId),
                new Claim("tid", AccountStatusApiFactory.CustomerTenantId),
                new Claim("role", nameof(UserRole.Admin)),
                new Claim("jti", Guid.NewGuid().ToString()),
            ]),
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(15),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>The account-status host on the rotation pool, with an in-memory pool that starts empty.</summary>
    private sealed class RotationPoolApiFactory : AccountStatusApiFactory
    {
        private readonly string _keyDirectory = Path.Join(Path.GetTempPath(), $"first-signing-key-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:KeyDirectory", _keyDirectory);
            builder.UseSetting("Identity:JwtKeyRotation:UseRotationPool", "true");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_keyDirectory))
                Directory.Delete(_keyDirectory, recursive: true);
        }
    }
}
