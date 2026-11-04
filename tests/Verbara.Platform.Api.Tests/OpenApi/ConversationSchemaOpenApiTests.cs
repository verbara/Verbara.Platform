using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// contact-history-opens-by-assignment task 1.4 (design D7, fixture <c>conversation-by-id-terminal.v1.json</c>): the
/// queue a conversation last passed through is stored (<c>Conversation.QueueId</c>, migration 027) but never put on the
/// wire, so the emitted OpenAPI <c>Conversation</c> schema keeps exactly the members it had before the change.
/// </summary>
public sealed class ConversationSchemaOpenApiTests
{
    /// <summary>The <c>Conversation</c> members on main at 0cfdb299 (v2.38.0), before this change.</summary>
    private static readonly string[] WireMembers =
    [
        "conversationId", "tenantId", "contactId", "channel", "owner", "state", "caseId", "createdAt", "closedAt",
        "updatedAt", "createdBy", "updatedBy", "voiceLinkedId", "queuePriority", "metadata", "sessions",
    ];

    [Fact]
    public async Task ConversationSchema_ShouldKeepItsMembersAndCarryNoQueueId_WhenTheQueueIsStored()
    {
        await using var factory = new OpenApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        var properties = document["components"]!["schemas"]!["Conversation"]!["properties"]!.AsObject()
            .Select(kv => kv.Key).ToList();
        properties.Should().BeEquivalentTo(WireMembers, "QueueId is [JsonIgnore], like LastMessageAt");
        properties.Should().NotContain(p => string.Equals(p, "queueId", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class OpenApiFactory : WebApplicationFactory<Program>
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
