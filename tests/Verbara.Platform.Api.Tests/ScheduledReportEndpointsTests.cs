using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests;

public sealed class ScheduledReportEndpointsTests : IClassFixture<UnifiedPlatformApiFactory>
{
    private const string ValidType = "agent_performance";
    private const string ValidCron = "0 9 * * *";

    private readonly HttpClient _client;

    public ScheduledReportEndpointsTests(UnifiedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    // ─── List ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListReports_ShouldReturn200_WhenAuthenticated()
    {
        await CreateReportAsync();

        var response = await _client.GetAsync("/api/admin/reports");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        JsonNode.Parse(content)!.AsArray().Should().NotBeNull();
    }

    // ─── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateReport_ShouldReturn201_WhenValidRequest()
    {
        var name = $"report-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new
        {
            name,
            type = ValidType,
            schedule = ValidCron,
            format = "pdf",
            isActive = true,
            recipients = "ops@example.com",
        });

        var response = await _client.PostAsync("/api/admin/reports", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["id"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        json["name"]!.GetValue<string>().Should().Be(name);
        json["type"]!.GetValue<string>().Should().Be(ValidType);
    }

    [Fact]
    public async Task CreateReport_ShouldReturn201_WhenScheduleEmpty()
    {
        var body = JsonContent.Create(new
        {
            name = $"report-{Guid.NewGuid():N}",
            type = ValidType,
            schedule = "",
            format = "csv",
            isActive = false,
        });

        var response = await _client.PostAsync("/api/admin/reports", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        // JSON null (or omitted) → the indexer returns a null JsonNode reference.
        json["nextRunAt"].Should().BeNull();
    }

    [Fact]
    public async Task CreateReport_ShouldReturn201_WhenUsingReportTypeAlias()
    {
        var body = JsonContent.Create(new
        {
            name = $"report-{Guid.NewGuid():N}",
            type = "",
            reportType = "queue_analytics",
            schedule = ValidCron,
            format = "pdf",
            isActive = true,
        });

        var response = await _client.PostAsync("/api/admin/reports", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["type"]!.GetValue<string>().Should().Be("queue_analytics");
    }

    [Fact]
    public async Task CreateReport_ShouldReturn400_WhenUnknownType()
    {
        var body = JsonContent.Create(new
        {
            name = $"report-{Guid.NewGuid():N}",
            type = "does_not_exist",
            schedule = ValidCron,
            format = "pdf",
            isActive = true,
        });

        var response = await _client.PostAsync("/api/admin/reports", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateReport_ShouldReturn400_WhenInvalidCron()
    {
        var body = JsonContent.Create(new
        {
            name = $"report-{Guid.NewGuid():N}",
            type = ValidType,
            schedule = "not-a-cron",
            format = "pdf",
            isActive = true,
        });

        var response = await _client.PostAsync("/api/admin/reports", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── Get ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetReport_ShouldReturn200_WhenExists()
    {
        var id = await CreateReportAsync();

        var response = await _client.GetAsync($"/api/admin/reports/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["id"]!.GetValue<string>().Should().Be(id);
    }

    [Fact]
    public async Task GetReport_ShouldReturn404_WhenNotFound()
    {
        var response = await _client.GetAsync($"/api/admin/reports/{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Update ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateReport_ShouldReturn200_WhenScheduleChanged()
    {
        var id = await CreateReportAsync();
        var newName = $"renamed-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new { name = newName, schedule = "0 10 * * *" });

        var response = await _client.PutAsync($"/api/admin/reports/{id}", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["name"]!.GetValue<string>().Should().Be(newName);
        json["schedule"]!.GetValue<string>().Should().Be("0 10 * * *");
    }

    [Fact]
    public async Task UpdateReport_ShouldReturn400_WhenUnknownType()
    {
        var id = await CreateReportAsync();
        var body = JsonContent.Create(new { type = "does_not_exist" });

        var response = await _client.PutAsync($"/api/admin/reports/{id}", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateReport_ShouldReturn400_WhenInvalidCron()
    {
        var id = await CreateReportAsync();
        var body = JsonContent.Create(new { schedule = "still-not-a-cron" });

        var response = await _client.PutAsync($"/api/admin/reports/{id}", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateReport_ShouldReturn404_WhenNotFound()
    {
        var body = JsonContent.Create(new { name = "whatever" });

        var response = await _client.PutAsync($"/api/admin/reports/{Guid.NewGuid():N}", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteReport_ShouldReturn204_WhenExists()
    {
        var id = await CreateReportAsync();

        var response = await _client.DeleteAsync($"/api/admin/reports/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteReport_ShouldReturn404_WhenNotFound()
    {
        var response = await _client.DeleteAsync($"/api/admin/reports/{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Manual trigger ───────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerManualRun_ShouldReturn202_WhenExists()
    {
        var id = await CreateReportAsync();

        var response = await _client.PostAsync($"/api/admin/reports/{id}/run", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["reportId"]!.GetValue<string>().Should().Be(id);
    }

    [Fact]
    public async Task TriggerManualRun_ShouldReturn404_WhenNotFound()
    {
        var response = await _client.PostAsync($"/api/admin/reports/{Guid.NewGuid():N}/run", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Execution history ────────────────────────────────────────────────────

    [Fact]
    public async Task GetExecutionHistory_ShouldReturn200_WhenExists()
    {
        var id = await CreateReportAsync();

        var response = await _client.GetAsync($"/api/admin/reports/{id}/history?limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        JsonNode.Parse(content)!.AsArray().Count.Should().Be(0);
    }

    [Fact]
    public async Task GetExecutionHistory_ShouldReturn404_WhenNotFound()
    {
        var response = await _client.GetAsync($"/api/admin/reports/{Guid.NewGuid():N}/history?limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Download execution ───────────────────────────────────────────────────

    [Fact]
    public async Task DownloadExecution_ShouldReturn404_WhenExecutionNotFound()
    {
        var id = await CreateReportAsync();

        var response = await _client.GetAsync(
            $"/api/admin/reports/{id}/history/{Guid.NewGuid():N}/download");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> CreateReportAsync()
    {
        var body = JsonContent.Create(new
        {
            name = $"report-{Guid.NewGuid():N}",
            type = ValidType,
            schedule = ValidCron,
            format = "pdf",
            isActive = true,
        });
        var resp = await _client.PostAsync("/api/admin/reports", body);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        return json["id"]!.GetValue<string>();
    }
}
