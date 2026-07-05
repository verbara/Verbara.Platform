using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests;

public sealed class DncListEndpointsTests : IClassFixture<UnifiedPlatformApiFactory>
{
    private const string BasePath = "/api/admin/dnc-lists";
    private readonly HttpClient _client;

    public DncListEndpointsTests(UnifiedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    // ─── CRUD ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListDncLists_ShouldReturn200_WithArrayContainingCreatedList()
    {
        var id = await CreateListAsync();

        var response = await _client.GetAsync(BasePath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        items.Select(n => n!["id"]!.GetValue<long>()).Should().Contain(id);
    }

    [Fact]
    public async Task CreateDncList_ShouldReturn201_WithTenantScope_WhenScopeOmitted()
    {
        var name = $"dnc-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new { name });

        var response = await _client.PostAsync(BasePath, body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["name"]!.GetValue<string>().Should().Be(name);
        json["scope"]!.GetValue<string>().Should().Be("Tenant");
        json["id"]!.GetValue<long>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateDncList_ShouldReturn201_WithGlobalScope_WhenScopeIsGlobal()
    {
        var name = $"dnc-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new { name, scope = "Global" });

        var response = await _client.PostAsync(BasePath, body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["scope"]!.GetValue<string>().Should().Be("Global");
    }

    [Fact]
    public async Task CreateDncList_ShouldDefaultToTenantScope_WhenScopeIsInvalid()
    {
        var name = $"dnc-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new { name, scope = "not-a-real-scope" });

        var response = await _client.PostAsync(BasePath, body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["scope"]!.GetValue<string>().Should().Be("Tenant");
    }

    [Fact]
    public async Task GetDncList_ShouldReturn200_WhenExists()
    {
        var id = await CreateListAsync();

        var response = await _client.GetAsync($"{BasePath}/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["id"]!.GetValue<long>().Should().Be(id);
    }

    [Fact]
    public async Task GetDncList_ShouldReturn404_WhenNotFound()
    {
        var response = await _client.GetAsync($"{BasePath}/999999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateDncList_ShouldReturn200_AndApplyChanges_WhenExists()
    {
        var id = await CreateListAsync();
        var newName = $"renamed-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new { name = newName, scope = "Global" });

        var response = await _client.PutAsync($"{BasePath}/{id}", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["name"]!.GetValue<string>().Should().Be(newName);
        json["scope"]!.GetValue<string>().Should().Be("Global");
    }

    [Fact]
    public async Task UpdateDncList_ShouldReturn404_WhenNotFound()
    {
        var body = JsonContent.Create(new { name = "ghost" });

        var response = await _client.PutAsync($"{BasePath}/999999999", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteDncList_ShouldReturn204_AndSubsequentGetReturns404()
    {
        var id = await CreateListAsync();

        var deleteResponse = await _client.DeleteAsync($"{BasePath}/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await _client.GetAsync($"{BasePath}/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Entries ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddEntry_ShouldReturn201_WithEntryData()
    {
        var id = await CreateListAsync();
        var phone = "15551230000";
        var body = JsonContent.Create(new { phoneNumber = phone, reason = "customer opt-out" });

        var response = await _client.PostAsync($"{BasePath}/{id}/entries", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["phoneNumber"]!.GetValue<string>().Should().Be(phone);
        json["reason"]!.GetValue<string>().Should().Be("customer opt-out");
    }

    [Fact]
    public async Task ListEntries_ShouldReturnPaginatedSlice_WhenOffsetAndLimitProvided()
    {
        var id = await CreateListAsync();
        for (var i = 0; i < 5; i++)
            await AddEntryAsync(id, $"1555000000{i}");

        var response = await _client.GetAsync($"{BasePath}/{id}/entries?offset=2&limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        items.Count.Should().Be(2);
    }

    [Fact]
    public async Task ListEntries_ShouldReturn200_WithEmptyArray_WhenListUnknown()
    {
        var response = await _client.GetAsync($"{BasePath}/888888888/entries");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        items.Count.Should().Be(0);
    }

    [Fact]
    public async Task RemoveEntry_ShouldReturn204_AndNumberNoLongerOnList()
    {
        var id = await CreateListAsync();
        var phone = "15557778888";
        await AddEntryAsync(id, phone);

        var removeResponse = await _client.DeleteAsync($"{BasePath}/{id}/entries/{phone}");
        removeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var checkResponse = await _client.GetAsync($"{BasePath}/{id}/check/{phone}");
        var json = JsonNode.Parse(await checkResponse.Content.ReadAsStringAsync())!;
        json["exists"]!.GetValue<bool>().Should().BeFalse();
    }

    // ─── Check ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckNumber_ShouldReturnExistsTrue_WhenNumberOnList()
    {
        var id = await CreateListAsync();
        var phone = "15551112222";
        await AddEntryAsync(id, phone);

        var response = await _client.GetAsync($"{BasePath}/{id}/check/{phone}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["exists"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CheckNumber_ShouldReturnExistsFalse_WhenNumberNotOnList()
    {
        var id = await CreateListAsync();

        var response = await _client.GetAsync($"{BasePath}/{id}/check/19998887777");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["exists"]!.GetValue<bool>().Should().BeFalse();
    }

    // ─── CSV Import ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportCsv_ShouldImportDataRows_SkippingHeadersAndBlankLines()
    {
        var id = await CreateListAsync();
        // Headers ("phone", "PHONE" case-insensitive, "phone_number") and the blank
        // line are skipped; multi-column rows keep the first column; quoted values
        // are unquoted. 4 numbers should be imported.
        var csv = string.Join('\n',
            "phone",
            "PHONE",
            "phone_number",
            "15551230001",
            "15551230002,label,extra",
            "\"15551230003\"",
            "",
            "15551230004");

        using var form = BuildCsvUpload(csv);
        var response = await _client.PostAsync($"{BasePath}/{id}/import", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["imported"]!.GetValue<int>().Should().Be(4);
        json["skipped"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task ImportCsv_ShouldReturn400_WhenBodyIsNotMultipart()
    {
        var id = await CreateListAsync();
        var body = JsonContent.Create(new { phone = "15550001111" });

        var response = await _client.PostAsync($"{BasePath}/{id}/import", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ImportCsv_ShouldReturn400_WhenNoFileUploaded()
    {
        var id = await CreateListAsync();
        using var form = new MultipartFormDataContent
        {
            { new StringContent("not-a-file"), "notfile" },
        };

        var response = await _client.PostAsync($"{BasePath}/{id}/import", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ImportCsv_ShouldReturn400_WhenFileIsEmpty()
    {
        var id = await CreateListAsync();
        using var form = BuildCsvUpload(string.Empty);

        var response = await _client.PostAsync($"{BasePath}/{id}/import", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<long> CreateListAsync()
    {
        var name = $"dnc-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new { name });
        var resp = await _client.PostAsync(BasePath, body);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        return json["id"]!.GetValue<long>();
    }

    private async Task AddEntryAsync(long listId, string phone, string? reason = null)
    {
        var body = JsonContent.Create(new { phoneNumber = phone, reason });
        var resp = await _client.PostAsync($"{BasePath}/{listId}/entries", body);
        resp.EnsureSuccessStatusCode();
    }

    private static MultipartFormDataContent BuildCsvUpload(string csv)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(fileContent, "file", "dnc.csv");
        return form;
    }
}
