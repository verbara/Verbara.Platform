// Back-compat tests: EnforcementMode is [Obsolete] in Pro v2.4.0-pro but kept functional until v2.5.0-pro.
#pragma warning disable CS0618

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

            // Disable license enforcement and provide dummy public key so
            // LicenseValidationHostedService starts without a real license file.
            services.Configure<LicenseOptions>(o => o.EnforcementMode = EnforcementMode.Disabled);
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            // All conditionally-registered stores (campaign, dialer config, analytics)
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
        });

        return base.CreateHost(builder);
    }
}
