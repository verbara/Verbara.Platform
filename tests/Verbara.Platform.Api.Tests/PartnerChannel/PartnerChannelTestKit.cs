using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.PartnerChannel;

/// <summary>
/// partner-channel-pilot-fixes: shared helpers for the Partner-channel regression tests — the frozen refusal
/// bodies of the change's fixtures, a shape check of a refusal against them, and direct tenant seeding into a
/// test host's <see cref="ITenantStore"/>.
/// </summary>
internal static class PartnerChannelTestKit
{
    public static readonly string[] NodeA = ["node-a"];
    public static readonly string[] NodeB = ["node-b"];

    /// <summary>The <c>body</c> of a frozen refusal fixture; <paramref name="key"/> picks one entry of a file that holds several.</summary>
    public static JsonObject FixtureBody(string file, string? key = null)
    {
        var path = Path.Join(AppContext.BaseDirectory, "PartnerChannel", "Fixtures", file);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var entry = key is null ? root : root[key]!.AsObject();
        return entry["body"]!.AsObject();
    }

    /// <summary>
    /// Asserts the response is <paramref name="status"/> with a body that has exactly the fixture's fields, in its
    /// order, each of the fixture's JSON kind (a null field may be null), and <c>error</c> equal to <paramref name="error"/>.
    /// Returns the parsed body so the caller can check the values.
    /// </summary>
    public static async Task<JsonObject> AssertRefusalAsync(
        HttpResponseMessage response, HttpStatusCode status, JsonObject fixtureBody, string error)
    {
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, raw);
        var body = JsonNode.Parse(raw)!.AsObject();
        body.Select(kv => kv.Key).Should().Equal(fixtureBody.Select(kv => kv.Key),
            "the refusal carries exactly the frozen fixture's fields, in its order");
        foreach (var (name, expected) in fixtureBody)
        {
            if (expected is null || body[name] is null)
                continue;
            body[name]!.GetValueKind().Should().Be(expected.GetValueKind(), $"{name} keeps the fixture's JSON type");
        }

        body["error"]!.GetValue<string>().Should().Be(error);
        return body;
    }

    public static async Task SeedTenantAsync(
        IServiceProvider services, string tenantId, TenantType type, string? parentTenantId, TenantPlan? plan,
        TenantOptions? options = null)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var metadata = new Dictionary<string, string>();
        if (plan is { } p)
            metadata["Plan"] = p.ToString();
        await store.UpsertAsync(new Tenant
        {
            TenantId = tenantId,
            Name = tenantId,
            Status = TenantStatus.Active,
            Type = type,
            ParentTenantId = parentTenantId,
            Options = options ?? new TenantOptions { MaxConcurrentChannels = 100, MaxActiveCampaigns = 10 },
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    public static async Task<Tenant?> GetTenantAsync(IServiceProvider services, string tenantId)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITenantStore>().GetAsync(tenantId);
    }
}
