using System.Net;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// dialer-sub-resources-stay-in-their-tenant: the holiday operations of a holiday calendar and the entry operations of
/// a caller-ID pool answer 404 for a calendar or pool outside the caller's tenant, and declare that 404 in the
/// emitted document. One row per operation, so a missing 404 names the operation that lost it.
/// </summary>
public sealed class DialerSubResourcesOpenApiTests : IClassFixture<SupervisorListenOpenApiTests.OpenApiFactory>
{
    private const string CalendarPath = "/api/v{version}/admin/holiday-calendars/{id}";
    private const string PoolPath = "/api/v{version}/admin/caller-id-pools/{id}";

    private readonly SupervisorListenOpenApiTests.OpenApiFactory _factory;

    public DialerSubResourcesOpenApiTests(SupervisorListenOpenApiTests.OpenApiFactory factory) => _factory = factory;

    /// <summary>The six operations, as path and lower-case method.</summary>
    public static TheoryData<string, string> NotFoundOperations => new()
    {
        { $"{CalendarPath}/holidays", "get" },
        { $"{CalendarPath}/holidays", "post" },
        { $"{CalendarPath}/holidays/{{holidayId}}", "delete" },
        { $"{PoolPath}/entries", "get" },
        { $"{PoolPath}/entries", "post" },
        { $"{PoolPath}/entries/{{entryId}}", "delete" },
    };

    [Theory]
    [MemberData(nameof(NotFoundOperations))]
    public async Task DialerSubResourceOperation_ShouldDeclareA404_WhenTheDocumentIsEmitted(string path, string method)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paths = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["paths"]!.AsObject();

        paths.ContainsKey(path).Should().BeTrue(
            $"the document names {path}; its dialer paths are {string.Join(", ", paths.Select(kv => kv.Key).Where(k => k.Contains("/admin/holiday-calendars/", StringComparison.Ordinal) || k.Contains("/admin/caller-id-pools/", StringComparison.Ordinal)))}");
        var operation = paths[path]![method];
        operation.Should().NotBeNull($"{path} has a {method} operation");
        var responses = operation!["responses"]!.AsObject();
        responses.ContainsKey("404").Should().BeTrue(
            $"{method} {path} answers 404 for a calendar or pool outside the caller's tenant; the operation declares {string.Join(", ", responses.Select(kv => kv.Key))}");
    }
}
