using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// <see cref="PlatformApiFactory"/>'s host, started through <see cref="EntryPointHostBuilder{TEntryPoint}"/> instead of the
/// factory's deferred builder, so a host whose start fails throws the start's own exception. For the tests that assert
/// on a startup failure (timed-tests-hold-on-a-loaded-runner F6): through the deferred builder they saw an
/// <see cref="ObjectDisposedException"/> instead whenever the failed start disposed the host before the factory looked at
/// it, which a loaded runner made the common case.
/// </summary>
public sealed class EntryPointStartPlatformApiFactory : WebApplicationFactory<Program>
{
    protected override IHostBuilder CreateHostBuilder()
    {
        // The defaults WebApplicationFactory gives its own deferred builder: Development (replaced by Testing in
        // CreateHost) and the application name of the entry assembly.
        var builder = new EntryPointHostBuilder<Program>();
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [HostDefaults.ApplicationKey] = typeof(Program).Assembly.GetName().Name ?? string.Empty,
        }));
        return builder;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        PlatformApiFactory.ConfigureTestHost(builder);
        return base.CreateHost(builder);
    }
}
