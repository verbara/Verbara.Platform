using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.EventStore;

/// <summary>
/// call-records-are-written-once task B.3c: the real Api <c>Program</c> composition booted with exactly the settings a
/// test names, so the startup probe of design D13a runs on the actual startup path, at registration, where it decides.
/// A setting whose value is <see langword="null"/> is not set at all (a connection string set to an empty string is not
/// unset: <c>Cluster ?? Postgres</c> would stop at it); <c>Asterisk:Ami:Hostname</c> is cleared unless the test sets it,
/// because <c>appsettings.json</c> ships <c>localhost</c>. Everything after registration is the
/// <see cref="AuthenticatedPlatformApiFactory"/> shape: the Verbara hosted services stripped and the licensing stubs.
/// </summary>
public sealed class ColocationProbeHostFactory(IReadOnlyDictionary<string, string?> settings)
    : WebApplicationFactory<Program>
{
    /// <summary>The test's AMI host. It names no reachable server: AMI is stubbed and the cluster manager stripped.</summary>
    public const string AmiHostname = "asterisk.colocation-probe.invalid";

    /// <summary>Builds the host: <see langword="null"/> when it started, else the exception its startup threw.</summary>
    public Exception? Boot()
    {
        try
        {
            _ = Services;
            return null;
        }
#pragma warning disable CA1031 // the test reads whatever the startup threw
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ex;
        }
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (!settings.ContainsKey("Asterisk:Ami:Hostname"))
            builder.UseSetting("Asterisk:Ami:Hostname", string.Empty);
        foreach (var (key, value) in settings)
        {
            if (value is not null)
                builder.UseSetting(key, value);
        }
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
        });
        return base.CreateHost(builder);
    }
}
