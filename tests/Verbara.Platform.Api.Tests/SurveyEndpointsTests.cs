using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests;

public sealed class SurveyEndpointsTests : IClassFixture<UnifiedPlatformApiFactory>
{
    private static readonly string[] s_choiceOptions = ["a", "b"];
    private readonly HttpClient _client;

    public SurveyEndpointsTests(UnifiedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    // ─── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSurvey_ShouldReturn201_WhenValidCsat()
    {
        var name = $"csat-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new
        {
            name,
            type = "Csat",
            questions = new[]
            {
                new { text = "How satisfied?", type = "Scale" },
            },
        });

        var response = await _client.PostAsync("/api/admin/surveys", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["id"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        node["name"]!.GetValue<string>().Should().Be(name);
        node["type"]!.GetValue<string>().Should().Be("Csat");
        node["isActive"]!.GetValue<bool>().Should().BeTrue();
        node["questions"]!.AsArray().Count.Should().Be(1);
    }

    [Fact]
    public async Task CreateSurvey_ShouldDefaultIsActiveTrue_WhenOmitted()
    {
        var body = JsonContent.Create(new
        {
            name = $"nps-{Guid.NewGuid():N}",
            type = "Nps",
            questions = new[]
            {
                new { text = "How likely to recommend?", type = "Scale" },
            },
        });

        var response = await _client.PostAsync("/api/admin/surveys", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["isActive"]!.GetValue<bool>().Should().BeTrue();
        node["type"]!.GetValue<string>().Should().Be("Nps");
    }

    [Fact]
    public async Task CreateSurvey_ShouldRespectIsActiveFalse_WhenProvided()
    {
        var body = JsonContent.Create(new
        {
            name = $"custom-{Guid.NewGuid():N}",
            type = "Custom",
            isActive = false,
            questions = new object[]
            {
                new { text = "Any comments?", type = "FreeText" },
                new { text = "Pick one", type = "Choice", options = s_choiceOptions },
            },
        });

        var response = await _client.PostAsync("/api/admin/surveys", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["isActive"]!.GetValue<bool>().Should().BeFalse();
        node["questions"]!.AsArray().Count.Should().Be(2);
    }

    // ─── List ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListSurveys_ShouldReturn200()
    {
        var response = await _client.GetAsync("/api/admin/surveys");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        JsonNode.Parse(content)!.AsArray().Should().NotBeNull();
    }

    [Fact]
    public async Task ListSurveys_ShouldIncludeCreatedSurvey()
    {
        var name = $"list-{Guid.NewGuid():N}";
        var id = await CreateSurveyAsync(name);

        var response = await _client.GetAsync("/api/admin/surveys");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        items.Any(n => n!["id"]!.GetValue<string>() == id).Should().BeTrue();
    }

    // ─── Get ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSurvey_ShouldReturn200_WhenExists()
    {
        var name = $"get-{Guid.NewGuid():N}";
        var id = await CreateSurveyAsync(name);

        var response = await _client.GetAsync($"/api/admin/surveys/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["id"]!.GetValue<string>().Should().Be(id);
        node["name"]!.GetValue<string>().Should().Be(name);
    }

    [Fact]
    public async Task GetSurvey_ShouldReturn404_WhenUnknownId()
    {
        var response = await _client.GetAsync($"/api/admin/surveys/{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Update ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateSurvey_ShouldReturn200_WhenAllFieldsProvided()
    {
        var id = await CreateSurveyAsync($"upd-{Guid.NewGuid():N}");
        var newName = $"renamed-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new
        {
            name = newName,
            type = "Nps",
            isActive = false,
            questions = new[]
            {
                new { text = "Updated question", type = "Scale" },
            },
        });

        var response = await _client.PutAsync($"/api/admin/surveys/{id}", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["id"]!.GetValue<string>().Should().Be(id);
        node["name"]!.GetValue<string>().Should().Be(newName);
        node["type"]!.GetValue<string>().Should().Be("Nps");
        node["isActive"]!.GetValue<bool>().Should().BeFalse();
        node["questions"]!.AsArray().Count.Should().Be(1);
    }

    [Fact]
    public async Task UpdateSurvey_ShouldPreserveFields_WhenEmptyBody()
    {
        var name = $"upd-empty-{Guid.NewGuid():N}";
        var id = await CreateSurveyAsync(name);
        var body = JsonContent.Create(new { });

        var response = await _client.PutAsync($"/api/admin/surveys/{id}", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["name"]!.GetValue<string>().Should().Be(name);
        node["type"]!.GetValue<string>().Should().Be("Csat");
        node["isActive"]!.GetValue<bool>().Should().BeTrue();
        node["questions"]!.AsArray().Count.Should().Be(1);
    }

    [Fact]
    public async Task UpdateSurvey_ShouldReturn404_WhenUnknownId()
    {
        var body = JsonContent.Create(new { name = "does-not-matter" });

        var response = await _client.PutAsync($"/api/admin/surveys/{Guid.NewGuid():N}", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSurvey_ShouldReturn204_WhenExists()
    {
        var id = await CreateSurveyAsync($"del-{Guid.NewGuid():N}");

        var response = await _client.DeleteAsync($"/api/admin/surveys/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterGet = await _client.GetAsync($"/api/admin/surveys/{id}");
        afterGet.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteSurvey_ShouldReturn204_WhenUnknownId()
    {
        var response = await _client.DeleteAsync($"/api/admin/surveys/{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─── Activate / Deactivate ────────────────────────────────────────────────

    [Fact]
    public async Task ActivateSurvey_ShouldDeactivate_WhenIsActiveFalse()
    {
        var id = await CreateSurveyAsync($"act-off-{Guid.NewGuid():N}");
        var body = JsonContent.Create(new { isActive = false });

        var response = await _client.PatchAsync($"/api/admin/surveys/{id}/activate", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["isActive"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task ActivateSurvey_ShouldActivate_WhenIsActiveTrue()
    {
        var id = await CreateSurveyAsync($"act-on-{Guid.NewGuid():N}", isActive: false);
        var body = JsonContent.Create(new { isActive = true });

        var response = await _client.PatchAsync($"/api/admin/surveys/{id}/activate", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["isActive"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ActivateSurvey_ShouldReturn404_WhenUnknownId()
    {
        var body = JsonContent.Create(new { isActive = true });

        var response = await _client.PatchAsync($"/api/admin/surveys/{Guid.NewGuid():N}/activate", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Analytics ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSurveySummary_ShouldReturn200WithZeroResponses_WhenNoData()
    {
        var id = await CreateSurveyAsync($"sum-{Guid.NewGuid():N}");

        var response = await _client.GetAsync($"/api/analytics/surveys/{id}/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["totalResponses"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task GetSurveyResponses_ShouldReturn200WithEmptyArray_WhenNoData()
    {
        var id = await CreateSurveyAsync($"resp-{Guid.NewGuid():N}");

        var response = await _client.GetAsync($"/api/analytics/surveys/{id}/responses");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        JsonNode.Parse(content)!.AsArray().Count.Should().Be(0);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> CreateSurveyAsync(string name, bool isActive = true)
    {
        var body = JsonContent.Create(new
        {
            name,
            type = "Csat",
            isActive,
            questions = new[]
            {
                new { text = "How satisfied?", type = "Scale" },
            },
        });
        var resp = await _client.PostAsync("/api/admin/surveys", body);
        resp.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        return node["id"]!.GetValue<string>();
    }
}
