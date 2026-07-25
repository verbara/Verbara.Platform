using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// End-to-end capture of the CORRECTED OpenAPI document for the residual contract shapes
/// (openapi-residual-contract-shapes, decision_ref Platform/ADR-0036). Boots the Api host in-memory
/// with <c>Platform:OpenApi:Enabled=true</c> (the same path ADR-0035's CI-runtime export uses),
/// fetches <c>/openapi/v1.json</c> through the live schema transformers, and asserts the three
/// residual shapes match their golden fixtures:
/// <list type="bullet">
///   <item><c>ComplianceRuleSummaryDto.severity</c> is the closed enum <c>[Info, Warning, Critical]</c>
///     (the one genuine producer fix) plus its sibling fields.</item>
///   <item><c>TopicTrendsResponse</c> emits <c>trends</c>/<c>totalAnalyzed</c> with no
///     <c>topics</c>/<c>from</c>/<c>to</c> (regression guard — no host change, D2).</item>
///   <item>the <c>PagedResult&lt;T&gt;</c> envelope declares the 7 envelope fields (verify-only,
///     the <c>PagedResultOf&lt;T&gt;</c> monomorphization is by-design, D3).</item>
/// </list>
/// When <c>CAPTURE_OPENAPI_PATH</c> is set it also writes the document to that path (the Stage-2 Web
/// handoff artifact) — otherwise it is a pure regression guard.
/// </summary>
public sealed class ResidualContractShapesCaptureTests
{
    private const string OpenApiDocumentPath = "/openapi/v1.json";

    private static async Task<JsonNode> FetchOpenApiDocumentAsync()
    {
        await using var factory = new CaptureFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(OpenApiDocumentPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();

        var capturePath = Environment.GetEnvironmentVariable("CAPTURE_OPENAPI_PATH");
        if (!string.IsNullOrEmpty(capturePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
            await File.WriteAllTextAsync(capturePath, body);
        }

        return JsonNode.Parse(body)!;
    }

    private static JsonObject Schemas(JsonNode doc) =>
        doc["components"]!["schemas"]!.AsObject();

    private static IReadOnlyList<string> PropertyNames(JsonNode schema) =>
        schema["properties"] is JsonObject props ? [.. props.Select(kvp => kvp.Key)] : [];

    private static IReadOnlyList<string> EnumValues(JsonNode schema) =>
        schema["enum"] is JsonArray arr ? [.. arr.Select(n => n!.GetValue<string>())] : [];

    [Fact]
    public async Task OpenApiDocument_ShouldDeclareSeverityAsClosedEnum_WhenComplianceRuleSummarySchemaEmitted()
    {
        var doc = await FetchOpenApiDocumentAsync();
        var schemas = Schemas(doc);

        schemas.Should().ContainKey("ComplianceRuleSummaryDto",
            "the compliance-summary endpoint surfaces the ComplianceRuleSummaryDto response shape");

        var rule = schemas["ComplianceRuleSummaryDto"]!;
        PropertyNames(rule).Should().BeEquivalentTo(
            ["ruleId", "ruleName", "severity", "occurrences", "sessionsAffected", "firstSeen", "lastSeen"],
            "the fixture pins these exact fields (compliance-rule-summary.v1.json)");

        var severity = rule["properties"]!["severity"]!;
        severity["type"]!.GetValue<string>().Should().Be("string",
            "severity is a closed set of string literals");
        // NB: string-collection Equal takes params string[] (no `because`) — the transformer narrows
        // severity to exactly the ComplianceSeverityBreakdownDto domain, in order.
        EnumValues(severity).Should().Equal("Info", "Warning", "Critical");
    }

    [Fact]
    public async Task OpenApiDocument_ShouldEmitTrends_WhenTopicTrendsResponseSchemaEmitted()
    {
        var doc = await FetchOpenApiDocumentAsync();
        var schemas = Schemas(doc);

        schemas.Should().ContainKey("TopicTrendsResponse",
            "the topic-trends endpoint surfaces the TopicTrendsResponse shape");

        var names = PropertyNames(schemas["TopicTrendsResponse"]!);
        names.Should().BeEquivalentTo(["trends", "totalAnalyzed"],
            "the emitted shape already matches topic-trends-response.v1.json — no host change (D2)");
        names.Should().NotContain("topics", "the stale `topics` name lived only in the Web shadow");
        names.Should().NotContain("from", "TopicTrendsResponse has no from/to window");
        names.Should().NotContain("to", "TopicTrendsResponse has no from/to window");
    }

    [Fact]
    public async Task OpenApiDocument_ShouldDeclarePagedResultEnvelope_WhenPagedResultSchemaEmitted()
    {
        var doc = await FetchOpenApiDocumentAsync();
        var schemas = Schemas(doc);

        // PagedResult<T> monomorphizes to one concrete `PagedResultOf<T>` component per element type
        // (by-design, D3). Assert the envelope fields on whichever concrete schema is emitted.
        var paged = schemas.FirstOrDefault(kvp => kvp.Key.StartsWith("PagedResultOf", StringComparison.Ordinal));
        paged.Value.Should().NotBeNull(
            "at least one PagedResultOf<T> envelope must be emitted (paged endpoints exist)");

        PropertyNames(paged.Value!).Should().BeEquivalentTo(
            ["items", "totalCount", "page", "pageSize", "totalPages", "hasNextPage", "hasPreviousPage"],
            "the envelope matches paged-result-envelope.v1.json field-for-field (D3, by-design)");
    }

    [Fact]
    public async Task OpenApiDocument_ShouldNotDeclareNumericStringUnion_WhenCaptured()
    {
        // Regression guard: the sibling ComplianceSeverityEnumTransformer must not have disturbed the
        // ADR-0036 NumericSchemaTruthTransformer — no numeric+string union may survive the document.
        await using var factory = new CaptureFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync(OpenApiDocumentPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var compact = body.Replace(" ", string.Empty).Replace("\n", string.Empty).Replace("\r", string.Empty);
        compact.Should().NotContain("[\"integer\",\"string\"]", "no integer/string union may survive");
        compact.Should().NotContain("[\"string\",\"integer\"]", "order-independent");
        compact.Should().NotContain("[\"number\",\"string\"]", "no number/string union may survive");
        compact.Should().NotContain("[\"string\",\"number\"]", "order-independent");
    }

    private sealed class CaptureFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureHostConfiguration(c =>
            {
                c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Platform:OpenApi:Enabled"] = "true",
                });
            });
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
        }
    }
}
