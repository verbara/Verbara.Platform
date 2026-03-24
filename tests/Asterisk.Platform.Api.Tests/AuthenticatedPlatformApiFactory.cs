using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.Analytics;
using Asterisk.Sdk.Pro.CallAnalytics.Store;
using Asterisk.Sdk.Pro.Dialer.Campaign;
using Asterisk.Sdk.Pro.Dialer.Contacts;
using Asterisk.Sdk.Pro.Dialer.Dispositions;
using Asterisk.Sdk.Pro.EventStore;
using Asterisk.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

/// <summary>
/// Factory that pre-seeds an authenticated API key so tests can call protected endpoints.
/// Also registers all in-memory store fallbacks so endpoints that depend on conditionally-
/// registered Postgres stores (campaign, analytics) resolve correctly without a database.
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
            // ── Auth ──────────────────────────────────────────────────────────
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

            // ── Licensing ─────────────────────────────────────────────────────
            services.Configure<LicenseOptions>(o => o.EnforcementMode = EnforcementMode.Disabled);
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            // ── Campaign stores (conditional on Postgres in Program.cs) ───────
            if (!services.Any(d => d.ServiceType == typeof(CampaignStoreBase)))
            {
                services.AddSingleton<InMemoryCampaignStore>();
                services.AddSingleton<CampaignStoreBase>(sp => sp.GetRequiredService<InMemoryCampaignStore>());
                services.AddSingleton<CampaignLifecycleManager>(sp =>
                    new CampaignLifecycleManager(
                        sp.GetRequiredService<CampaignStoreBase>(),
                        sp.GetRequiredService<ILogger<CampaignLifecycleManager>>()));
            }

            if (!services.Any(d => d.ServiceType == typeof(ContactListStoreBase)))
            {
                services.AddSingleton<InMemoryContactListStore>();
                services.AddSingleton<ContactListStoreBase>(sp => sp.GetRequiredService<InMemoryContactListStore>());
            }

            if (!services.Any(d => d.ServiceType == typeof(DispositionCodeStoreBase)))
            {
                services.AddSingleton<InMemoryDispositionCodeStore>();
                services.AddSingleton<DispositionCodeStoreBase>(sp => sp.GetRequiredService<InMemoryDispositionCodeStore>());
            }

            // ── Analytics stores (conditional on Postgres in Program.cs) ──────
            if (!services.Any(d => d.ServiceType == typeof(ICompletedSessionStore)))
                services.AddSingleton<ICompletedSessionStore, InMemoryCompletedSessionStore>();
            if (!services.Any(d => d.ServiceType == typeof(ICallAnalyticsStore)))
                services.AddSingleton<ICallAnalyticsStore, InMemoryCallAnalyticsStore>();
            if (!services.Any(d => d.ServiceType == typeof(IIntervalSnapshotStore)))
                services.AddSingleton<IIntervalSnapshotStore, InMemoryIntervalSnapshotStore>();
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
