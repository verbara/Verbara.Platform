using System.Net;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// dialer-references-stay-in-their-tenant task 1.3: the outbound-route create and update answer 400 when a trunk id is
/// not a trunk of the caller's tenant, and the emitted document declares that 400 with an <c>ErrorResponse</c> body;
/// the create declares its 201 and no 200.
/// The schema checks of the exported document read only <c>components.schemas</c>, so only this operation test sees a
/// response code. One row per operation and code, so a missing code names the operation that lost it.
/// </summary>
public sealed class OutboundRoutesOpenApiTests : IClassFixture<SupervisorListenOpenApiTests.OpenApiFactory>
{
    private const string RoutesPath = "/api/v{version}/admin/routes";

    private readonly SupervisorListenOpenApiTests.OpenApiFactory _factory;

    public OutboundRoutesOpenApiTests(SupervisorListenOpenApiTests.OpenApiFactory factory) => _factory = factory;

    /// <summary>The operation, as path and lower-case method, and the response code it declares.</summary>
    public static TheoryData<string, string, string> DeclaredResponses => new()
    {
        { RoutesPath, "post", "400" },
        { $"{RoutesPath}/{{id}}", "put", "400" },
        { RoutesPath, "post", "201" },
    };

    /// <summary>The operations that answer 400, as path and lower-case method.</summary>
    public static TheoryData<string, string> BadRequestOperations => new()
    {
        { RoutesPath, "post" },
        { $"{RoutesPath}/{{id}}", "put" },
    };

    [Theory]
    [MemberData(nameof(DeclaredResponses))]
    public async Task RouteOperation_ShouldDeclareTheResponse_WhenTheDocumentIsEmitted(
        string path, string method, string code)
    {
        var responses = await ResponsesAsync(path, method);

        responses.ContainsKey(code).Should().BeTrue(
            $"{method} {path} answers {code}; the operation declares {string.Join(", ", responses.Select(kv => kv.Key))}");
    }

    [Theory]
    [MemberData(nameof(BadRequestOperations))]
    public async Task RouteOperation_ShouldDeclareErrorResponseOn400_WhenTheDocumentIsEmitted(string path, string method)
    {
        var responses = await ResponsesAsync(path, method);

        var schemaRef = responses["400"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>()
            ?? "(no schema $ref)";
        schemaRef.Should().Be("#/components/schemas/ErrorResponse",
            $"{method} {path} answers 400 with an ErrorResponse body; its 400 is {responses["400"]?.ToJsonString() ?? "undeclared"}");
    }

    [Fact]
    public async Task CreateRoute_ShouldNotDeclare200_WhenTheDocumentIsEmitted()
    {
        var responses = await ResponsesAsync(RoutesPath, "post");

        responses.ContainsKey("200").Should().BeFalse(
            $"creating a route answers 201, never 200; the operation declares {string.Join(", ", responses.Select(kv => kv.Key))}");
    }

    /// <summary>The <c>responses</c> object of one operation of the emitted document.</summary>
    private async Task<JsonObject> ResponsesAsync(string path, string method)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paths = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["paths"]!.AsObject();

        paths.ContainsKey(path).Should().BeTrue(
            $"the document names {path}; its route paths are {string.Join(", ", paths.Select(kv => kv.Key).Where(k => k.Contains("/admin/routes", StringComparison.Ordinal)))}");
        var operation = paths[path]![method];
        operation.Should().NotBeNull($"{path} has a {method} operation");
        return operation!["responses"]!.AsObject();
    }
}
