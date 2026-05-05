using System.Net;
using Verbara.Sdk.Pro.Licensing;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// R5.3 Phase B Task B.7 (D.1) — verifies the OpenAPI document endpoint is:
/// 1. Reachable when the environment is non-Production (Development) OR when
///    <c>Platform:OpenApi:Enabled=true</c> is set in configuration.
/// 2. Hidden by default outside Development (the Testing environment models
///    the "shipped to Production without the env-var" baseline).
///
/// Platform.Api wires <see cref="Microsoft.AspNetCore.OpenApi"/> (built-in
/// .NET 10, AOT-friendly) to emit the spec at <c>/openapi/v1.json</c>; the
/// Scalar UI is mounted at <c>/scalar/v1</c>. The JSON path is the contract
/// surface for downstream tooling (codegen, contract diff) so it is the
/// natural place to assert.
/// </summary>
public sealed class SwaggerEndpointTests
{
    private const string OpenApiDocumentPath = "/openapi/v1.json";

    [Fact]
    public async Task OpenApiJson_ShouldBeAccessible_WhenExplicitlyEnabled()
    {
        await using var factory = new OpenApiEnabledFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(OpenApiDocumentPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the OpenAPI document must be exposed when Platform:OpenApi:Enabled=true");

        var contentType = response.Content.Headers.ContentType?.MediaType;
        contentType.Should().Be("application/json",
            "the OpenAPI endpoint must serve a JSON document");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace();
        body.Should().Contain("\"openapi\"",
            "a valid OpenAPI document must start with the version discriminator");
    }

    [Fact]
    public async Task OpenApiJson_ShouldBe404_WhenOpenApiNotEnabled()
    {
        await using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(OpenApiDocumentPath);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the OpenAPI document must not be exposed unless Platform:OpenApi:Enabled=true is set");
    }

    /// <summary>
    /// Mirrors <see cref="PlatformApiFactory"/> but flips
    /// <c>Platform:OpenApi:Enabled=true</c> via in-memory configuration so the
    /// OpenAPI services + endpoints are wired even though the environment is
    /// "Testing" (i.e. not Development). This proves the production opt-in
    /// path works without changing the environment.
    /// </summary>
    private sealed class OpenApiEnabledFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            // UseSetting flows through both the host config and the WebHost
            // config, so the value is visible to Program.cs when it reads
            // builder.Configuration.GetValue<bool>("Platform:OpenApi:Enabled")
            // during initial wiring (before the host is built).
            builder.ConfigureHostConfiguration(c =>
            {
                c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Platform:OpenApi:Enabled"] = "true",
                });
            });

            builder.ConfigureServices(services =>
            {
                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
                services.Configure<LicenseOptions>(o => o.EnforcementMode = EnforcementMode.Disabled);
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
            });

            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Belt + suspenders: also push the setting through the WebHost
            // configuration so Program.cs sees it irrespective of whether it
            // reads from the host or web-host config provider chain.
            builder.UseSetting("Platform:OpenApi:Enabled", "true");
        }
    }
}
