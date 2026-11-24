using System.Net;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// realtime-objects-stay-in-their-tenant (review round 1): Management create answers 409 with <c>ErrorResponse</c> when
/// the tenant id is unavailable and with <c>PlanAboveParentRefusal</c> when the plan exceeds the parent's, so its
/// emitted 409 declares both shapes. The schema checks of the exported document read only <c>components.schemas</c>,
/// so only this operation test notices an undeclared 409 body.
/// </summary>
public sealed class ManagementTenantCreateOpenApiTests : IClassFixture<SupervisorListenOpenApiTests.OpenApiFactory>
{
    private const string CreatePath = "/api/v{version}/management/tenants";

    private readonly SupervisorListenOpenApiTests.OpenApiFactory _factory;

    public ManagementTenantCreateOpenApiTests(SupervisorListenOpenApiTests.OpenApiFactory factory) => _factory = factory;

    private async Task<(JsonNode Document, JsonObject Operation)> CreateOperationAsync()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var paths = document["paths"]!.AsObject();
        var path = paths.Select(kv => kv.Key).FirstOrDefault(k => k.TrimEnd('/') == CreatePath);
        path.Should().NotBeNull($"the document names the create route; its management paths are {string.Join(", ", paths.Select(kv => kv.Key).Where(k => k.Contains("/management/tenants", StringComparison.Ordinal)))}");
        var operation = paths[path!]!["post"];
        operation.Should().NotBeNull("the create route is a POST");
        return (document, operation!.AsObject());
    }

    private static HashSet<string> SchemaNames(JsonNode? schema)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        if (k == "$ref" && v is JsonValue r)
                            names.Add(r.GetValue<string>().Split('/')[^1]);
                        else
                            Walk(v);
                    }
                    break;
                case JsonArray a:
                    foreach (var v in a)
                        Walk(v);
                    break;
            }
        }
        Walk(schema);
        return names;
    }

    [Fact]
    public async Task CreateOperation_ShouldDeclareBothConflictBodies_WhenTheDocumentIsEmitted()
    {
        var (document, operation) = await CreateOperationAsync();

        var conflict = operation["responses"]?["409"];
        (conflict is not null).Should().BeTrue("the create route answers 409");
        var schema = conflict?["content"]?["application/json"]?["schema"];
        SchemaNames(schema).Should().Contain(["ErrorResponse", "PlanAboveParentRefusal"],
            $"the 409 is an unavailable tenant id or a plan above the parent's; it declares {conflict?.ToJsonString()}");
        var components = document["components"]!["schemas"]!.AsObject();
        components.ContainsKey("ErrorResponse").Should().BeTrue("each referenced body has a component schema");
        components.ContainsKey("PlanAboveParentRefusal").Should().BeTrue("each referenced body has a component schema");
    }
}
