using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// Document-wide guard: every operation that documents a 4xx response also documents at least one
/// 2xx response. An explicit <c>.Produces&lt;Refusal&gt;(4xx)</c> on an <c>IResult</c> handler
/// suppresses ASP.NET's inferred default 200, so the operation silently loses its success response
/// (PR #367 did this to seven routes). Boots the Api host in-memory with
/// <c>Platform:OpenApi:Enabled=true</c>, the same path the CI-runtime export uses.
/// </summary>
public sealed class SuccessResponseCaptureTests
{
    private const string OpenApiDocumentPath = "/openapi/v1.json";

    private static readonly string[] HttpMethods = ["get", "put", "post", "delete", "patch", "head", "options", "trace"];

    // Pre-existing violators, measured across the whole document before the #367 fix (they were
    // already 4xx-only at v2.27.0). Listed so this guard can land without fixing them here; each entry
    // must stay a violator, so fixing one forces its removal from this list.
    private static readonly HashSet<string> PreExistingViolators =
    [
        // Typed Results<FileContentHttpResult, …>: FileContentHttpResult carries no OpenAPI metadata, so
        // the audio 200 is not inferred. Pre-existing at v2.27.0; out of scope for the #367 fix.
        "POST /api/v{version}/admin/csat/templates/{id}/preview-voice",
    ];

    [Fact]
    public async Task OpenApiDocument_ShouldDocumentSuccessResponse_WhenOperationDocumentsClientError()
    {
        var doc = await FetchOpenApiDocumentAsync();

        var operations = doc["paths"]!.AsObject()
            .SelectMany(p => HttpMethods.Select(m => (Key: $"{m.ToUpperInvariant()} {p.Key}", Responses: p.Value![m]?["responses"] as JsonObject)))
            .Where(op => op.Responses is not null)
            .Select(op => (op.Key, Codes: op.Responses!.Select(kvp => kvp.Key).ToList()))
            .ToList();

        operations.Should().HaveCountGreaterThan(100, "the guard must measure a real document, not an empty one");

        var violators = operations
            .Where(op => op.Codes.Exists(c => c.StartsWith('4')) && !op.Codes.Exists(c => c.StartsWith('2')))
            .Select(op => $"{op.Key} [{string.Join(", ", op.Codes)}]")
            .ToList();

        var unexpected = violators.Where(v => !PreExistingViolators.Contains(OperationKey(v))).ToList();
        unexpected.Should().BeEmpty(
            "every operation that documents a 4xx must also document its 2xx success response; violators:\n{0}",
            string.Join("\n", unexpected));

        var stale = PreExistingViolators.Where(k => !violators.Exists(v => OperationKey(v) == k)).ToList();
        stale.Should().BeEmpty("a fixed or removed pre-existing violator must leave the allowlist");
    }

    private static string OperationKey(string violator) => violator[..violator.IndexOf(" [", StringComparison.Ordinal)];

    private static async Task<JsonNode> FetchOpenApiDocumentAsync()
    {
        await using var factory = new CaptureFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(OpenApiDocumentPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
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
