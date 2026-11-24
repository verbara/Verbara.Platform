using System.Net;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// routing-skills-stay-in-their-tenant: the three agent skill operations answer 404 for an agent outside the caller's
/// tenant and declare that 404 in the emitted document, together with the success code each route answers — 200 with
/// an <c>AgentSkillDto</c> array on the list, 201 with an <c>AgentSkillDto</c> on the assign, 204 on the remove — not
/// the bare 200 an untyped result declares. One row per operation, so a missing 404 names the operation that lost it.
/// </summary>
public sealed class AgentSkillRoutesOpenApiTests : IClassFixture<SupervisorListenOpenApiTests.OpenApiFactory>
{
    private const string AgentSkillsPath = "/api/v{version}/admin/agents/{agentId}/skills";
    private const string AgentSkillPath = AgentSkillsPath + "/{skillName}";

    private readonly SupervisorListenOpenApiTests.OpenApiFactory _factory;

    public AgentSkillRoutesOpenApiTests(SupervisorListenOpenApiTests.OpenApiFactory factory) => _factory = factory;

    /// <summary>The three operations, as path and lower-case method.</summary>
    public static TheoryData<string, string> NotFoundOperations => new()
    {
        { AgentSkillsPath, "get" },
        { AgentSkillsPath, "post" },
        { AgentSkillPath, "delete" },
    };

    [Theory]
    [MemberData(nameof(NotFoundOperations))]
    public async Task AgentSkillOperation_ShouldDeclareA404_WhenTheDocumentIsEmitted(string path, string method)
    {
        var responses = await ResponsesAsync(path, method);

        responses.ContainsKey("404").Should().BeTrue(
            $"{method} {path} answers 404 for an agent outside the caller's tenant; the operation declares {string.Join(", ", responses.Select(kv => kv.Key))}");
    }

    [Fact]
    public async Task GetAgentSkills_ShouldDeclare200WithAnAgentSkillArray_WhenTheDocumentIsEmitted()
    {
        var responses = await ResponsesAsync(AgentSkillsPath, "get");

        responses.ContainsKey("200").Should().BeTrue();
        var schema = responses["200"]!["content"]?["application/json"]?["schema"]
            ?? throw new InvalidOperationException("the list answers a JSON body, but its 200 declares no JSON schema");
        (schema["type"]?.GetValue<string>()).Should().Be("array");
        SchemaName(schema["items"]).Should().Be("AgentSkillDto");
    }

    [Fact]
    public async Task AssignSkill_ShouldDeclare201WithAnAgentSkillAndNo200_WhenTheDocumentIsEmitted()
    {
        var responses = await ResponsesAsync(AgentSkillsPath, "post");

        responses.ContainsKey("200").Should().BeFalse(
            $"the assign answers 201, not 200; the operation declares {string.Join(", ", responses.Select(kv => kv.Key))}");
        responses.ContainsKey("201").Should().BeTrue(
            $"the assign answers 201; the operation declares {string.Join(", ", responses.Select(kv => kv.Key))}");
        SchemaName(responses["201"]!["content"]?["application/json"]?["schema"]).Should().Be("AgentSkillDto");
    }

    [Fact]
    public async Task RemoveAgentSkill_ShouldDeclare204AndNo200_WhenTheDocumentIsEmitted()
    {
        var responses = await ResponsesAsync(AgentSkillPath, "delete");

        responses.ContainsKey("200").Should().BeFalse(
            $"the remove answers 204, not 200; the operation declares {string.Join(", ", responses.Select(kv => kv.Key))}");
        responses.ContainsKey("204").Should().BeTrue(
            $"the remove answers 204; the operation declares {string.Join(", ", responses.Select(kv => kv.Key))}");
    }

    private async Task<JsonObject> ResponsesAsync(string path, string method)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paths = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["paths"]!.AsObject();

        paths.ContainsKey(path).Should().BeTrue(
            $"the document names {path}; its agent paths are {string.Join(", ", paths.Select(kv => kv.Key).Where(k => k.Contains("/admin/agents/", StringComparison.Ordinal)))}");
        var operation = paths[path]![method];
        operation.Should().NotBeNull($"{path} has a {method} operation");
        return operation!["responses"]!.AsObject();
    }

    /// <summary>The component name a schema refers to, or <see langword="null"/> when it is no reference.</summary>
    private static string? SchemaName(JsonNode? schema)
    {
        var reference = schema?["$ref"]?.GetValue<string>();
        return reference?["#/components/schemas/".Length..];
    }
}
