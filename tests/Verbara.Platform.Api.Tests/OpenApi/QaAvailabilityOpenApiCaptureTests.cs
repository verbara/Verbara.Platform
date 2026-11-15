using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// The emitted OpenAPI document of
/// <c>GET /api/v1/analytics/qa-availability</c>. Every member of the body is always written, so the schema lists all of
/// them as required, and the members that can be null as nullable; the response-schema manifest lists the schema so CI's
/// <c>verify-openapi-fixture.py</c> guards it for the console's generated client.
/// </summary>
public sealed class QaAvailabilityOpenApiCaptureTests
{
    private const string Path = "/api/v{version}/analytics/qa-availability";

    private static readonly string[] Members =
        ["available", "analysisRunning", "reason", "transcriptSource", "speakerMode", "topicsConfigured", "complianceConfigured", "sentimentSupported", "backlog"];

    private static readonly string[] NullableScalars =
        ["reason", "transcriptSource", "speakerMode", "topicsConfigured", "complianceConfigured", "sentimentSupported"];

    private static async Task<JsonNode> DocumentAsync()
    {
        await using var factory = new CaptureFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static IReadOnlyList<string> Names(JsonNode? node) =>
        node switch
        {
            JsonObject obj => [.. obj.Select(kv => kv.Key)],
            JsonArray arr => [.. arr.Select(n => n!.GetValue<string>())],
            _ => [],
        };

    private static IReadOnlyList<string> Types(JsonNode property) =>
        property["type"] is JsonArray arr ? [.. arr.Select(n => n!.GetValue<string>())] : [property["type"]!.GetValue<string>()];

    [Fact]
    public async Task QaAvailabilityRoute_ShouldDocumentItsBody_WhenTheDocumentIsEmitted()
    {
        var document = await DocumentAsync();

        var responses = document["paths"]![Path]!["get"]!["responses"]!;

        responses["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>()
            .Should().Be("#/components/schemas/QaAvailabilityDto");
    }

    [Fact]
    public async Task QaAvailabilitySchema_ShouldRequireEveryMember_AndMarkTheNullableOnesNullable()
    {
        var schema = (await DocumentAsync())["components"]!["schemas"]!["QaAvailabilityDto"]!;

        Names(schema["properties"]).Should().Equal(Members);
        Names(schema["required"]).Should().BeEquivalentTo(Members, "every member is written, null or not");
        foreach (var member in NullableScalars)
            Types(schema["properties"]![member]!).Should().Contain("null", $"{member} can be null");
        schema["properties"]!["backlog"]!.ToJsonString().Should().Contain("null", "backlog is null until analysis runs");
    }

    [Fact]
    public async Task ResponseSchemaManifest_ShouldListQaAvailabilityWithTheDocumentsMembers_WhenTheDocumentIsEmitted()
    {
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(
            System.IO.Path.Join(RepoRoot(), "tests", "fixtures", "openapi", "response-schema-manifest.v1.json")))!;
        var emitted = Names((await DocumentAsync())["components"]!["schemas"]!["QaAvailabilityDto"]!["properties"]);

        var listed = manifest["groups"]!["analytics"]!["schemas"]!["QaAvailabilityDto"];

        listed.Should().NotBeNull("the analytics group of the manifest lists QaAvailabilityDto, so CI guards it");
        Names(listed).Should().Equal(emitted).And.Equal(Members);
    }

    [Fact]
    public async Task QaListRoute_ShouldDocumentA400_WhenTheDocumentIsEmitted()
    {
        var document = await DocumentAsync();

        var responses = document["paths"]!["/api/v{version}/analytics/qa"]!["get"]!["responses"]!;

        responses["400"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>()
            .Should().Be("#/components/schemas/ErrorResponse", "a minScore outside 0-100 is refused with a 400");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Join(dir.FullName, "Verbara.Platform.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

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
}
