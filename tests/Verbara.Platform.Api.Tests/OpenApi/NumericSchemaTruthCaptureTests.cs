using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// End-to-end capture of the CORRECTED OpenAPI document (openapi-numeric-schema-truth,
/// Platform/ADR-0036). Boots the Api host in-memory with <c>Platform:OpenApi:Enabled=true</c>
/// (the same path ADR-0035's CI-runtime export uses), fetches <c>/openapi/v1.json</c> through
/// the live <see cref="NumericSchemaTruthTransformer"/>, and asserts NO numeric+string union
/// (<c>["integer","string"]</c> / <c>["number","string"]</c>) survives. When
/// <c>CAPTURE_OPENAPI_PATH</c> is set it also writes the document to that path (the change's
/// fixtures dir) for the Stage-2 (Web) handoff — otherwise it is a pure regression guard.
/// </summary>
public sealed class NumericSchemaTruthCaptureTests
{
    private const string OpenApiDocumentPath = "/openapi/v1.json";

    [Fact]
    public async Task OpenApiDocument_ShouldDeclareNumericsSingleTyped_WhenTransformerRegistered()
    {
        await using var factory = new CaptureFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(OpenApiDocumentPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();

        // The .NET 10 artifact renders numeric unions as ["integer","string"] / ["number","string"]
        // in the serialized JSON. After the transformer, none may remain (whitespace-insensitive check).
        var compact = body.Replace(" ", string.Empty).Replace("\n", string.Empty).Replace("\r", string.Empty);
        compact.Should().NotContain("[\"integer\",\"string\"]",
            "the transformer must strip every integer/string union from the document");
        compact.Should().NotContain("[\"string\",\"integer\"]",
            "order-independent: no integer/string union may survive");
        compact.Should().NotContain("[\"number\",\"string\"]",
            "the transformer must strip every number/string union from the document");
        compact.Should().NotContain("[\"string\",\"number\"]",
            "order-independent: no number/string union may survive");

        var capturePath = Environment.GetEnvironmentVariable("CAPTURE_OPENAPI_PATH");
        if (!string.IsNullOrEmpty(capturePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
            await File.WriteAllTextAsync(capturePath, body);
        }
    }

    private sealed class CaptureFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
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
                services.AddAllProFeaturesLicensed();
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
            });
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Platform:OpenApi:Enabled", "true");
        }
    }
}
