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

namespace Asterisk.Platform.Api.Tests;

public sealed class PlatformApiFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Disable license enforcement and provide dummy public key so
            // LicenseValidationHostedService starts without a real license file.
            services.Configure<LicenseOptions>(o => o.EnforcementMode = EnforcementMode.Disabled);
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            // Campaign stores — registered conditionally on Postgres connection string in
            // Program.cs; provide in-memory fallbacks so all endpoints resolve correctly.
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

            // Analytics stores — also registered conditionally on Postgres connection string.
            if (!services.Any(d => d.ServiceType == typeof(ICompletedSessionStore)))
                services.AddSingleton<ICompletedSessionStore, InMemoryCompletedSessionStore>();
            if (!services.Any(d => d.ServiceType == typeof(ICallAnalyticsStore)))
                services.AddSingleton<ICallAnalyticsStore, InMemoryCallAnalyticsStore>();
            if (!services.Any(d => d.ServiceType == typeof(IIntervalSnapshotStore)))
                services.AddSingleton<IIntervalSnapshotStore, InMemoryIntervalSnapshotStore>();
        });

        return base.CreateHost(builder);
    }
}
