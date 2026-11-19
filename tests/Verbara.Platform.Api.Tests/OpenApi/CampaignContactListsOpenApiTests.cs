using System.Net;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// contact-lists-stay-in-their-tenant task 3.3: the campaign contact-list and disposition-code operations that answer
/// 404 for a campaign, list or code outside the caller's tenant or campaign declare that 404 in the emitted document.
/// The schema checks of the exported document read only <c>components.schemas</c>, so only this operation test sees a
/// response code. One row per operation, so a missing 404 names the operation that lost it.
/// </summary>
public sealed class CampaignContactListsOpenApiTests : IClassFixture<SupervisorListenOpenApiTests.OpenApiFactory>
{
    private const string CampaignPath = "/api/v{version}/admin/campaigns/{id}";

    private readonly SupervisorListenOpenApiTests.OpenApiFactory _factory;

    public CampaignContactListsOpenApiTests(SupervisorListenOpenApiTests.OpenApiFactory factory) => _factory = factory;

    /// <summary>The nine operations, as path and lower-case method.</summary>
    public static TheoryData<string, string> NotFoundOperations => new()
    {
        { $"{CampaignPath}/contact-lists", "get" },
        { $"{CampaignPath}/contact-lists", "post" },
        { $"{CampaignPath}/contact-lists/{{listId}}", "delete" },
        { $"{CampaignPath}/contact-lists/{{listId}}/import", "post" },
        { $"{CampaignPath}/contact-lists/{{listId}}/contacts", "get" },
        { $"{CampaignPath}/dispositions", "get" },
        { $"{CampaignPath}/dispositions", "post" },
        { $"{CampaignPath}/dispositions/{{codeId}}", "put" },
        { $"{CampaignPath}/dispositions/{{codeId}}", "delete" },
    };

    [Theory]
    [MemberData(nameof(NotFoundOperations))]
    public async Task CampaignOperation_ShouldDeclareA404_WhenTheDocumentIsEmitted(string path, string method)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paths = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["paths"]!.AsObject();

        paths.ContainsKey(path).Should().BeTrue(
            $"the document names {path}; its campaign paths are {string.Join(", ", paths.Select(kv => kv.Key).Where(k => k.Contains("/admin/campaigns/", StringComparison.Ordinal)))}");
        var operation = paths[path]![method];
        operation.Should().NotBeNull($"{path} has a {method} operation");
        var responses = operation!["responses"]!.AsObject();
        responses.ContainsKey("404").Should().BeTrue(
            $"{method} {path} answers 404 outside the caller's tenant or campaign; the operation declares {string.Join(", ", responses.Select(kv => kv.Key))}");
    }

    /// <summary>
    /// The contacts page answers the dialer's contact (list membership, status, attempts), not the CRM contact of
    /// <c>/contacts</c>; the two must not share one schema, or the document describes the wrong body.
    /// </summary>
    [Fact]
    public async Task ListContacts_ShouldDescribeTheDialerContact_WhenTheDocumentIsEmitted()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var schemas = document["components"]!["schemas"]!.AsObject();

        var page = Resolve(schemas, document["paths"]![$"{CampaignPath}/contact-lists/{{listId}}/contacts"]!["get"]!
            ["responses"]!["200"]!["content"]!["application/json"]!["schema"]!);
        var item = Resolve(schemas, page["properties"]!["items"]!["items"]!);
        var properties = item["properties"]!.AsObject().Select(kv => kv.Key).ToList();

        properties.Should().Contain(["contactListId", "status", "attempts", "lastPhoneDialed", "phoneNumbers"]);
        properties.Should().NotContain("channelConsent", "the CRM contact's members are not in the contacts page");
    }

    private static JsonNode Resolve(JsonObject schemas, JsonNode schema)
    {
        var reference = schema["$ref"]?.GetValue<string>();
        return reference is null ? schema : schemas[reference["#/components/schemas/".Length..]]!;
    }
}
