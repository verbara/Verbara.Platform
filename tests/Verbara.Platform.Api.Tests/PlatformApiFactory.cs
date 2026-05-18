using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests;

public sealed class PlatformApiFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // ── Asterisk SDK stubs (no real AMI/ARI connections in tests) ────
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);

            // v2.5.0-pro: substitute ILicenseStatus with all features licensed
            // so LicenseGateMiddleware short-circuits to next() in tests.
            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            // All conditionally-registered stores (campaign, dialer config, analytics)
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
        });

        return base.CreateHost(builder);
    }
}
