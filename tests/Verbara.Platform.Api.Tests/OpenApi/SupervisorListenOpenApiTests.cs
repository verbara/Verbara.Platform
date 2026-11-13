using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// agent-assist-sessions-stay-in-their-tenant task 1.5 (design D8, audit F2): the transcript listen operation's emitted
/// document. The schema checks of the exported document read only <c>components.schemas</c>, so only these operation
/// tests notice the request body or the 403. Two tests, so a failure of one assertion cannot mask the other.
/// </summary>
public sealed class SupervisorListenOpenApiTests : IClassFixture<SupervisorListenOpenApiTests.OpenApiFactory>
{
    private const string ListenPath = "/api/v{version}/supervisor/sessions/{sessionId}/listen";

    private readonly OpenApiFactory _factory;

    public SupervisorListenOpenApiTests(OpenApiFactory factory) => _factory = factory;

    private async Task<JsonObject> ListenOperationAsync()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        var paths = document["paths"]!.AsObject();
        paths.ContainsKey(ListenPath).Should().BeTrue(
            $"the document names the listen route; its supervisor session paths are {string.Join(", ", paths.Select(kv => kv.Key).Where(k => k.Contains("/supervisor/sessions", StringComparison.Ordinal)))}");
        var operation = paths[ListenPath]!["post"];
        operation.Should().NotBeNull("the listen route is a POST");
        return operation!.AsObject();
    }

    [Fact]
    public async Task ListenOperation_ShouldDeclareAnOptionalRequestBody_WhenTheDocumentIsEmitted()
    {
        var operation = await ListenOperationAsync();

        var requestBody = operation["requestBody"];
        var required = requestBody?["required"]?.GetValue<bool>() ?? false;
        required.Should().BeFalse(
            $"the body is optional and ignored; the operation declares {requestBody?.ToJsonString() ?? "no requestBody"}");
    }

    [Fact]
    public async Task ListenOperation_ShouldDeclareABodyless403_WhenTheDocumentIsEmitted()
    {
        var operation = await ListenOperationAsync();

        var responses = operation["responses"]!.AsObject();
        responses.ContainsKey("403").Should().BeTrue(
            $"credentials with no user id are refused with 403; the operation declares {string.Join(", ", responses.Select(kv => kv.Key))}");
        responses["403"]!.AsObject().ContainsKey("content").Should().BeFalse("the 403 carries no body");
    }

    /// <summary>A host that emits the OpenAPI document, as <c>ConversationSchemaOpenApiTests</c> builds one.</summary>
    public sealed class OpenApiFactory : WebApplicationFactory<Program>
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
}
