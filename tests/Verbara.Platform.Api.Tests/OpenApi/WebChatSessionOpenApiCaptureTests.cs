using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>The emitted OpenAPI document of <c>POST /webchat/sessions</c> carries the identity members of the fixture.</summary>
public sealed class WebChatSessionOpenApiCaptureTests
{
    private sealed class CaptureFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:OpenApi:Enabled"] = "true",
            }));
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

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("Platform:OpenApi:Enabled", "true");
    }

    [Fact]
    public async Task SessionSchemas_ShouldDeclareTheIdentityMembers_WhenTheDocumentIsEmitted()
    {
        await using var factory = new CaptureFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var schemas = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["components"]!["schemas"]!;
        var request = schemas["CreateSessionRequest"]!["properties"]!.AsObject().Select(kv => kv.Key);
        request.Should().Contain(["tenantId", "visitorId", "visitorName", "visitorEmail", "pageContext", "identityToken"]);
        var responseSchema = schemas["CreateSessionResponse"]!;
        responseSchema["properties"]!.AsObject().Select(kv => kv.Key)
            .Should().Contain(["sessionId", "wsUrl", "visitorAddress", "contactId", "conversationId", "identityLevel", "identityTokenRefusal"]);
        responseSchema["required"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().NotContain("identityTokenRefusal", "the member is absent unless a token was refused");
    }
}
