using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

/// <summary>
/// Factory that pre-seeds an authenticated API key so tests can call protected endpoints.
/// </summary>
public sealed class AuthenticatedPlatformApiFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "test-api-key-12345";
    public const string TestTenantId = "tenant-test-001";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Replace the IApiKeyStore with a substitute that returns our test key
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IApiKeyStore));
            if (descriptor is not null)
                services.Remove(descriptor);

            var store = Substitute.For<IApiKeyStore>();
            var apiKey = new ApiKey
            {
                KeyId = EntityId.From("test-key-id"),
                TenantId = new TenantId(TestTenantId),
                Name = "Test Key",
                HashedKey = s_hashedKey,
                Scopes = ["*"],
                IsRevoked = false,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            store.GetByHashAsync(s_hashedKey, Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<ApiKey?>(apiKey));

            services.AddSingleton(store);
        });

        return base.CreateHost(builder);
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TestTenantId);
        return client;
    }

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}
