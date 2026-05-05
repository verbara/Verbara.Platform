using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Audit;

/// <summary>
/// R5.2 PB.1 — coverage for the new audit log viewer endpoints
/// (<c>GET /admin/audit/events</c> + <c>GET /admin/audit/export</c>). Pairs the
/// host-tenant + Management API key seeded in <see cref="PlatformAdminApiFactory"/>
/// with the new filter/export shape to exercise filtering, RBAC, retention
/// header, and streaming serialization end-to-end.
/// </summary>
public sealed class AuditEndpointsFilterTests : IClassFixture<PlatformAdminApiFactory>
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    public AuditEndpointsFilterTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task GetEvents_ShouldFilterByActionPrefix_WhenPrefixProvided()
    {
        var tenant = new TenantId(PlatformAdminApiFactory.HostTenantId);
        await SeedAuditAsync(tenant, action: "mfa.admin.reset", actorId: "actor-1");
        await SeedAuditAsync(tenant, action: "mfa.admin.sessions_revoked", actorId: "actor-2");
        await SeedAuditAsync(tenant, action: "user.created", actorId: "actor-3");

        var response = await _client.GetAsync("/api/v1/admin/audit/events?actionPrefix=mfa.admin.&pageSize=200");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedDto<AuditEventDtoLite>>(s_jsonOptions);
        paged.Should().NotBeNull();
        paged!.Items.Should().Contain(e => e.Action == "mfa.admin.reset");
        paged.Items.Should().Contain(e => e.Action == "mfa.admin.sessions_revoked");
        paged.Items.Should().NotContain(e => e.Action == "user.created");
        // Retention header is always set so the UI can render the disclosure.
        response.Headers.Should().ContainKey("X-Audit-Retention-Days");
    }

    [Fact]
    public async Task GetEvents_ShouldFilterByActor_WhenActorProvided()
    {
        var tenant = new TenantId(PlatformAdminApiFactory.HostTenantId);
        await SeedAuditAsync(tenant, action: "user.updated", actorId: "alice@actor.test");
        await SeedAuditAsync(tenant, action: "user.updated", actorId: "bob@actor.test");

        var response = await _client.GetAsync(
            "/api/v1/admin/audit/events?actor=alice&pageSize=200");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedDto<AuditEventDtoLite>>(s_jsonOptions);
        paged!.Items.Should().Contain(e => e.ActorId == "alice@actor.test");
        paged.Items.Should().NotContain(e => e.ActorId == "bob@actor.test");
    }

    [Fact]
    public async Task GetEvents_ShouldFilterByTimeRange_WhenFromAndToProvided()
    {
        var tenant = new TenantId(PlatformAdminApiFactory.HostTenantId);
        var anchor = DateTimeOffset.UtcNow;
        await SeedAuditAsync(tenant,
            action: "range.before",
            actorId: "actor-x",
            occurredAt: anchor.AddDays(-30));
        await SeedAuditAsync(tenant,
            action: "range.middle",
            actorId: "actor-x",
            occurredAt: anchor.AddDays(-3));
        await SeedAuditAsync(tenant,
            action: "range.after",
            actorId: "actor-x",
            occurredAt: anchor.AddDays(5));

        var from = Uri.EscapeDataString(anchor.AddDays(-7).ToString("O", CultureInfo.InvariantCulture));
        var to = Uri.EscapeDataString(anchor.ToString("O", CultureInfo.InvariantCulture));

        var response = await _client.GetAsync(
            $"/api/v1/admin/audit/events?actionPrefix=range.&from={from}&to={to}&pageSize=200");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedDto<AuditEventDtoLite>>(s_jsonOptions);
        paged!.Items.Should().Contain(e => e.Action == "range.middle");
        paged.Items.Should().NotContain(e => e.Action == "range.before");
        paged.Items.Should().NotContain(e => e.Action == "range.after");
    }

    [Fact]
    public async Task GetEvents_ShouldReturn403_WhenMissingPermission()
    {
        await using var nonAdmin = new NonAdminAuthenticatedApiFactory();
        using var nonAdminClient = nonAdmin.CreateAuthenticatedClient();

        var response = await nonAdminClient.GetAsync("/api/v1/admin/audit/events");

        // PlatformAdminAuthorizationHandler refuses (Agent role + non-host tenant
        // lacking audit.read), surfacing as 403 from the authorization pipeline.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Export_ShouldStreamCsv_WhenFormatCsv()
    {
        var tenant = new TenantId(PlatformAdminApiFactory.HostTenantId);
        await SeedAuditAsync(tenant, action: "csv.export.row1", actorId: "actor-csv-a");
        await SeedAuditAsync(tenant, action: "csv.export.row2", actorId: "actor-csv-b");

        var response = await _client.GetAsync(
            "/api/v1/admin/audit/export?format=csv&actionPrefix=csv.export.");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");
        var body = await response.Content.ReadAsStringAsync();
        // First line is the canonical header; rows follow newline-delimited.
        body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Should().StartWith("entryId,occurredAt,action");
        body.Should().Contain("csv.export.row1");
        body.Should().Contain("csv.export.row2");
    }

    [Fact]
    public async Task Export_ShouldStreamJson_WhenFormatJson()
    {
        var tenant = new TenantId(PlatformAdminApiFactory.HostTenantId);
        await SeedAuditAsync(tenant, action: "json.export.row1", actorId: "actor-json");

        var response = await _client.GetAsync(
            "/api/v1/admin/audit/export?format=json&actionPrefix=json.export.");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("[");
        body.Should().EndWith("]");
        body.Should().Contain("\"action\":\"json.export.row1\"");
    }

    private async Task SeedAuditAsync(
        TenantId tenantId,
        string action,
        string actorId,
        DateTimeOffset? occurredAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        await store.SaveAsync(new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = tenantId,
            Action = action,
            Category = "admin",
            Severity = "info",
            ActorId = actorId,
            ActorType = "user",
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        }, CancellationToken.None);
    }

    private sealed record PagedDto<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

    private sealed record AuditEventDtoLite(
        string EntryId,
        string Action,
        string ActorId,
        string Severity,
        string Status);
}
