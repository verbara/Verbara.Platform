using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// outbound-calls-reach-the-trunk (design D4): the emitted OpenAPI document of the voice dial and transfer routes.
/// Both declare 200 and 409; <c>trunkId</c> is a required, nullable integer, and the members the serializer omits when
/// null (<c>correlationId</c>, <c>error</c>) are optional. Boots the Api host in-memory with
/// <c>Platform:OpenApi:Enabled=true</c> and an AMI host name, which maps the voice routes (nothing connects to it).
/// </summary>
public sealed class VoiceResponsesOpenApiCaptureTests
{
    private static async Task<JsonNode> FetchOpenApiDocumentAsync()
    {
        await using var factory = new CaptureFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static IReadOnlyList<string> Required(JsonNode schema) =>
        schema["required"] is JsonArray arr ? [.. arr.Select(n => n!.GetValue<string>())] : [];

    private static IReadOnlyList<string> Types(JsonNode property) =>
        property["type"] is JsonArray arr ? [.. arr.Select(n => n!.GetValue<string>())] : [property["type"]!.GetValue<string>()];

    [Fact]
    public async Task VoiceRoutes_ShouldDocumentTheSuccessAndTheTrunkNotDialableConflict()
    {
        var paths = (await FetchOpenApiDocumentAsync())["paths"]!;

        foreach (var path in new[] { "/api/v{version}/voice/dial", "/api/v{version}/conversations/{id}/voice-transfer" })
        {
            var responses = paths[path]!["post"]!["responses"]!.AsObject().Select(kv => kv.Key).ToList();
            responses.Should().Contain(["200", "409"], $"{path} documents its success and the trunk-not-dialable refusal");
        }
    }

    [Theory]
    [InlineData("VoiceDialResponse", new[] { "accepted", "trunkId" }, new[] { "correlationId", "error" })]
    [InlineData("VoiceTransferResponse", new[] { "accepted", "trunkId" }, new[] { "error" })]
    public async Task VoiceResponseSchema_ShouldRequireTrunkId_AndLeaveTheOmittedMembersOptional(
        string name, string[] required, string[] optional)
    {
        var schema = (await FetchOpenApiDocumentAsync())["components"]!["schemas"]![name]!;

        Required(schema).Should().BeEquivalentTo(required);
        Types(schema["properties"]!["trunkId"]!).Should().BeEquivalentTo(["null", "integer"]);
        foreach (var member in optional)
            schema["properties"]![member].Should().NotBeNull($"{member} is still a documented member");
    }

    private sealed class CaptureFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:OpenApi:Enabled"] = "true",
                ["Asterisk:Ami:Hostname"] = "ami.invalid",
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

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Platform:OpenApi:Enabled", "true");
            builder.UseSetting("Asterisk:Ami:Hostname", "ami.invalid");
        }
    }
}
