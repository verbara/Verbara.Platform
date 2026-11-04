using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// contact-history-opens-by-assignment task 2.5 (design D14): the emitted OpenAPI document of the contact's conversation
/// history and of a conversation's history. The four DTOs and the closed <c>PagedResult&lt;ContactConversationSummaryDto&gt;</c>
/// envelope are emitted with the fixtures' member names; the members <c>ApiJsonContext</c> omits when null
/// (<c>WhenWritingNull</c>) are documented but not required; both routes declare their refusals; and the response-schema
/// manifest lists all five with the document's member names. Boots the Api host in-memory with
/// <c>Platform:OpenApi:Enabled=true</c>, the path the CI-runtime export uses.
/// </summary>
public sealed class ContactHistoryOpenApiCaptureTests
{
    private const string ContactConversationsPath = "/api/v{version}/contacts/{id}/conversations";
    private const string HistoryPath = "/api/v{version}/conversations/{id}/history";

    /// <summary>The five schemas of design D14, each with every member the document must declare, in fixture order.</summary>
    public static TheoryData<string, string[], string[]> Schemas() => new()
    {
        {
            "ContactConversationSummaryDto",
            [
                "id", "channel", "state", "createdAt", "closedAt", "durationSeconds", "closedReason", "queueId", "queueName",
                "agentId", "agentDisplayName", "disposition", "lastMessageAt", "lastMessage", "heldByCaller",
            ],
            ["id", "channel", "state", "createdAt", "lastMessageAt", "heldByCaller"]
        },
        { "ConversationDispositionDto", ["nodeId", "label"], ["nodeId"] },
        { "ConversationLastMessageDto", ["direction", "blockType", "preview", "createdAt"], ["direction", "blockType", "preview", "createdAt"] },
        { "MessageHistoryPageDto", ["items", "hasMore", "nextBefore"], ["items", "hasMore"] },
        {
            "PagedResultOfContactConversationSummaryDto",
            ["items", "totalCount", "page", "pageSize", "totalPages", "hasNextPage", "hasPreviousPage"],
            ["items", "totalCount", "page", "pageSize"]
        },
    };

    private static JsonNode? _document;

    private static async Task<JsonNode> DocumentAsync()
    {
        if (_document is not null)
            return _document;

        await using var factory = new CaptureFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return _document;
    }

    private static List<string> Names(JsonNode? node) =>
        node switch
        {
            JsonObject obj => [.. obj.Select(kv => kv.Key)],
            JsonArray arr => [.. arr.Select(n => n!.GetValue<string>())],
            _ => [],
        };

    [Theory]
    [MemberData(nameof(Schemas))]
    public async Task HistorySchema_ShouldRequireOnlyTheAlwaysWrittenMembers_WhenTheDocumentIsEmitted(
        string name, string[] members, string[] required)
    {
        var schema = (await DocumentAsync())["components"]!["schemas"]![name];

        schema.Should().NotBeNull($"{name} is emitted in components/schemas (design D14)");
        Names(schema!["properties"]).Should().Equal(members, $"{name} declares every member of its fixture");
        Names(schema["required"]).Should().BeEquivalentTo(
            required, $"a member of {name} omitted when null (WhenWritingNull) is optional, every other one is required");
    }

    [Fact]
    public async Task ContactConversationsRoute_ShouldDocumentTheSummaryEnvelopeAndItsRefusals_WhenTheDocumentIsEmitted()
    {
        var document = await DocumentAsync();
        var responses = document["paths"]![ContactConversationsPath]!["get"]!["responses"]!;

        Names(responses).Should().BeEquivalentTo(["200", "403", "404"]);
        responses["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>()
            .Should().Be("#/components/schemas/PagedResultOfContactConversationSummaryDto", "the item type is the summary (design D5)");
        responses["403"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>()
            .Should().Be("#/components/schemas/ErrorResponse");
        responses["404"]!["content"].Should().BeNull("an unknown contact answers 404 with an empty body");
        document["components"]!["schemas"]!["PagedResultOfContactConversationSummaryDto"]!["properties"]!["items"]!["items"]!["$ref"]!
            .GetValue<string>().Should().Be("#/components/schemas/ContactConversationSummaryDto");
    }

    [Fact]
    public async Task HistoryRoute_ShouldDocumentThePageAndItsRefusals_WhenTheDocumentIsEmitted()
    {
        var document = await DocumentAsync();
        var get = document["paths"]![HistoryPath]!["get"]!;
        var responses = get["responses"]!;

        Names(responses).Should().BeEquivalentTo(["200", "400", "403", "404"]);
        responses["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>()
            .Should().Be("#/components/schemas/MessageHistoryPageDto");
        foreach (var code in new[] { "400", "403" })
            responses[code]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>()
                .Should().Be("#/components/schemas/ErrorResponse", $"{code} carries the error code");
        responses["404"]!["content"].Should().BeNull("an unknown conversation answers 404 with an empty body");
        get["parameters"]!.AsArray().Select(p => p!["name"]!.GetValue<string>())
            .Should().BeEquivalentTo(["id", "limit", "before"]);
        document["components"]!["schemas"]!["MessageHistoryPageDto"]!["properties"]!["items"]!["items"]!["$ref"]!
            .GetValue<string>().Should().Be("#/components/schemas/Message", "the page carries the existing Message shape");
    }

    [Theory]
    [MemberData(nameof(Schemas))]
    public async Task ResponseSchemaManifest_ShouldListTheSchemaWithTheDocumentsMembers_WhenTheDocumentIsEmitted(
        string name, string[] members, string[] required)
    {
        _ = required;
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Join(RepoRoot(), "tests", "fixtures", "openapi", "response-schema-manifest.v1.json")))!;
        var emitted = Names((await DocumentAsync())["components"]!["schemas"]![name]!["properties"]);

        var listed = manifest["groups"]!["agent"]!["schemas"]![name];

        listed.Should().NotBeNull($"the agent group of the manifest lists {name} (design D14), so CI's verify-openapi-fixture.py guards it");
        Names(listed).Should().Equal(emitted).And.Equal(members);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Verbara.Platform.slnx")))
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
