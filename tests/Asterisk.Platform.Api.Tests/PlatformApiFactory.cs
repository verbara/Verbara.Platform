using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Platform.Api.Tests;

public sealed class PlatformApiFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Use test environment so appsettings.Test.json is loaded if present
        builder.UseEnvironment("Testing");
        return base.CreateHost(builder);
    }
}
