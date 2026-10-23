using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Sdk.Pro.Dialer.Results;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-places-calls-end-to-end (design D3 point 7, D9; task 2.2): the real Api <c>Program</c> with the dialer
/// registered (a <c>ConnectionStrings:Dialer</c>), the cluster's Postgres transport (a <c>ConnectionStrings:Cluster</c>)
/// and the Api's Asterisk node in the cluster's initial nodes (<c>Asterisk:Ami</c>). Everything else is
/// <see cref="PlatformApiFactory"/>: the Verbara hosted services stripped, AMI and the server stubbed, the in-memory
/// stores and the licensing stubs.
/// </summary>
/// <remarks>
/// <see cref="AuthenticatedPlatformApiFactory.StubVerbaraHostedServices"/> strips Pro's
/// <see cref="CallOutcomeObserver"/> with every other <c>Verbara</c>-named hosted service. This host keeps the
/// descriptors <c>Program.cs</c> registered for it, through <c>AddProDialer</c>, and re-adds those same descriptors
/// (the <c>AccountStatusApiFactory</c> precedent: never a test-made registration), so a host or a Pro build that stops
/// registering the observer leaves <see cref="CapturedObservers"/> empty and no observer running.
/// </remarks>
public sealed class DialerHostWiringFactory : WebApplicationFactory<Program>
{
    /// <summary>The Api's AMI host. It names no reachable server: AMI is stubbed and the cluster manager stripped.</summary>
    public const string AmiHostname = "asterisk.dialer-wiring.invalid";

    private readonly string _dialerConnectionString;
    private readonly string _clusterConnectionString;

    /// <summary>Creates the host over the two connection strings (a Testcontainers Postgres).</summary>
    public DialerHostWiringFactory(string dialerConnectionString, string clusterConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dialerConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterConnectionString);
        _dialerConnectionString = dialerConnectionString;
        _clusterConnectionString = clusterConnectionString;
    }

    /// <summary>
    /// The production <see cref="IHostedService"/> descriptors whose implementation type is
    /// <see cref="CallOutcomeObserver"/>, as <c>Program.cs</c> left them before the stubs ran.
    /// </summary>
    public IReadOnlyList<ServiceDescriptor> CapturedObservers { get; private set; } = [];

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Dialer", _dialerConnectionString);
        builder.UseSetting("ConnectionStrings:Cluster", _clusterConnectionString);
        builder.UseSetting("Asterisk:Ami:Hostname", AmiHostname);
        builder.UseSetting("Asterisk:Ami:Port", "5038");
        builder.UseSetting("Asterisk:Ami:Username", "dialer-wiring");
        builder.UseSetting("Asterisk:Ami:Password", "dialer-wiring");
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            var observers = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(CallOutcomeObserver))
                .ToList();
            CapturedObservers = observers;

            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
            foreach (var observer in observers)
                services.Add(observer);

            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
        });

        return base.CreateHost(builder);
    }
}
