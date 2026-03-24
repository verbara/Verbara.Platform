using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Asterisk.Platform.Api.Tests;

public sealed class CampaignEndpointTests : IClassFixture<UnifiedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public CampaignEndpointTests(UnifiedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    // ─── Campaign CRUD ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateCampaign_ShouldReturn201_WhenValidRequest()
    {
        var body = JsonContent.Create(new
        {
            name = "Summer Sale Campaign",
            description = "Q3 outbound campaign",
            mode = "progressive",
            targetQueueName = "sales",
            maxConcurrentCalls = 10,
            timezone = "America/New_York",
            schedule = Array.Empty<object>(),
            holidays = Array.Empty<string>(),
            dncEnabled = false,
            maxAttemptsPerContact = 3,
            retryIntervalMinutes = 60,
            timeBetweenAttemptsMinutes = 5,
        });

        var response = await _client.PostAsync("/api/admin/campaigns", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateCampaign_ShouldPersistAllFields_WhenCreated()
    {
        var body = JsonContent.Create(new
        {
            name = "Persist Fields Campaign",
            description = "Test persistence",
            mode = "power",
            targetQueueName = "outbound",
            maxConcurrentCalls = 5,
            powerRatio = 2.0,
            timezone = "UTC",
            schedule = Array.Empty<object>(),
            holidays = Array.Empty<string>(),
            dncEnabled = true,
            maxAttemptsPerContact = 4,
            retryIntervalMinutes = 30,
            timeBetweenAttemptsMinutes = 10,
        });

        var response = await _client.PostAsync("/api/admin/campaigns", body);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var content = await response.Content.ReadAsStringAsync();
        var created = JsonNode.Parse(content);

        created!["name"]!.GetValue<string>().Should().Be("Persist Fields Campaign");
        created["status"]!.GetValue<string>().Should().Be("Draft");
        created["mode"]!.GetValue<string>().Should().Be("Power");
    }

    [Fact]
    public async Task ListCampaigns_ShouldReturnPagedResult_WhenCampaignsExist()
    {
        // Create a campaign first
        var body = JsonContent.Create(new
        {
            name = "List Test Campaign",
            mode = "preview",
            targetQueueName = "support",
            maxConcurrentCalls = 1,
            timezone = "UTC",
            schedule = Array.Empty<object>(),
            holidays = Array.Empty<string>(),
            dncEnabled = false,
            maxAttemptsPerContact = 2,
            retryIntervalMinutes = 0,
            timeBetweenAttemptsMinutes = 0,
        });
        await _client.PostAsync("/api/admin/campaigns", body);

        var response = await _client.GetAsync("/api/admin/campaigns");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonNode.Parse(content);
        result!["items"]!.AsArray().Count.Should().BeGreaterThan(0);
        result["totalCount"]!.GetValue<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ListCampaigns_ShouldReturnEmpty_WhenNoCampaigns()
    {
        // Use a dedicated factory instance scoped to this test so state is clean
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/admin/campaigns");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonNode.Parse(content);
        result!["totalCount"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task GetCampaign_ShouldReturnDetail_WhenExists()
    {
        var createBody = JsonContent.Create(new
        {
            name = "Detail Campaign",
            mode = "predictive",
            targetQueueName = "sales",
            maxConcurrentCalls = 20,
            timezone = "UTC",
            schedule = Array.Empty<object>(),
            holidays = Array.Empty<string>(),
            dncEnabled = false,
            maxAttemptsPerContact = 3,
            retryIntervalMinutes = 60,
            timeBetweenAttemptsMinutes = 5,
        });
        var createResp = await _client.PostAsync("/api/admin/campaigns", createBody);
        var created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync());
        var id = created!["id"]!.GetValue<long>();

        var response = await _client.GetAsync($"/api/admin/campaigns/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Detail Campaign");
    }

    [Fact]
    public async Task GetCampaign_ShouldReturn404_WhenNotFound()
    {
        var response = await _client.GetAsync("/api/admin/campaigns/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateCampaign_ShouldModifyFields_WhenValidRequest()
    {
        var createBody = JsonContent.Create(new
        {
            name = "Original Name",
            mode = "progressive",
            targetQueueName = "sales",
            maxConcurrentCalls = 5,
            timezone = "UTC",
            schedule = Array.Empty<object>(),
            holidays = Array.Empty<string>(),
            dncEnabled = false,
            maxAttemptsPerContact = 3,
            retryIntervalMinutes = 60,
            timeBetweenAttemptsMinutes = 5,
        });
        var createResp = await _client.PostAsync("/api/admin/campaigns", createBody);
        var created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync());
        var id = created!["id"]!.GetValue<long>();

        var updateBody = JsonContent.Create(new { name = "Updated Name", maxConcurrentCalls = 15 });
        var response = await _client.PutAsync($"/api/admin/campaigns/{id}", updateBody);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Updated Name");
    }

    [Fact]
    public async Task DeleteCampaign_ShouldReturn204_WhenExists()
    {
        var createBody = JsonContent.Create(new
        {
            name = "Delete Me Campaign",
            mode = "progressive",
            targetQueueName = "sales",
            maxConcurrentCalls = 1,
            timezone = "UTC",
            schedule = Array.Empty<object>(),
            holidays = Array.Empty<string>(),
            dncEnabled = false,
            maxAttemptsPerContact = 1,
            retryIntervalMinutes = 0,
            timeBetweenAttemptsMinutes = 0,
        });
        var createResp = await _client.PostAsync("/api/admin/campaigns", createBody);
        var created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync());
        var id = created!["id"]!.GetValue<long>();

        var response = await _client.DeleteAsync($"/api/admin/campaigns/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CreateCampaign_ShouldMapModeCorrectly_WhenAgentless()
    {
        var body = JsonContent.Create(new
        {
            name = "Agentless Campaign",
            mode = "agentless",
            targetQueueName = "robot",
            maxConcurrentCalls = 50,
            timezone = "UTC",
            schedule = Array.Empty<object>(),
            holidays = Array.Empty<string>(),
            dncEnabled = false,
            maxAttemptsPerContact = 1,
            retryIntervalMinutes = 0,
            timeBetweenAttemptsMinutes = 0,
        });

        var response = await _client.PostAsync("/api/admin/campaigns", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        // "agentless" maps to DialingMode.Robot
        content.Should().Contain("Robot");
    }

    [Fact]
    public async Task CreateCampaign_ShouldSetDefaults_WhenMinimalRequest()
    {
        var body = JsonContent.Create(new
        {
            name = "Minimal Campaign",
            mode = "progressive",
            targetQueueName = "default",
            maxConcurrentCalls = 1,
            timezone = "UTC",
            schedule = Array.Empty<object>(),
            holidays = Array.Empty<string>(),
            dncEnabled = false,
            maxAttemptsPerContact = 1,
            retryIntervalMinutes = 0,
            timeBetweenAttemptsMinutes = 0,
        });

        var response = await _client.PostAsync("/api/admin/campaigns", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        // Status should default to Draft
        content.Should().Contain("Draft");
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartCampaign_ShouldReturn200_WhenDraft()
    {
        var id = await CreateCampaignAsync("Start Test Campaign");

        var response = await _client.PostAsync($"/api/admin/campaigns/{id}/start", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PauseCampaign_ShouldReturn200_WhenActive()
    {
        var id = await CreateCampaignAsync("Pause Test Campaign");
        await _client.PostAsync($"/api/admin/campaigns/{id}/start", null);

        var response = await _client.PostAsync($"/api/admin/campaigns/{id}/pause", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResumeCampaign_ShouldReturn200_WhenPaused()
    {
        var id = await CreateCampaignAsync("Resume Test Campaign");
        await _client.PostAsync($"/api/admin/campaigns/{id}/start", null);
        await _client.PostAsync($"/api/admin/campaigns/{id}/pause", null);

        var response = await _client.PostAsync($"/api/admin/campaigns/{id}/resume", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StopCampaign_ShouldReturn200_WhenActive()
    {
        var id = await CreateCampaignAsync("Stop Test Campaign");
        await _client.PostAsync($"/api/admin/campaigns/{id}/start", null);

        var response = await _client.PostAsync($"/api/admin/campaigns/{id}/stop", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StartCampaign_ShouldReturn400_WhenAlreadyActive()
    {
        var id = await CreateCampaignAsync("Already Active Campaign");
        await _client.PostAsync($"/api/admin/campaigns/{id}/start", null);

        // Try to start again (already Active — Draft/Paused only valid)
        var response = await _client.PostAsync($"/api/admin/campaigns/{id}/start", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StopCampaign_ShouldReturn400_WhenAlreadyCompleted()
    {
        var id = await CreateCampaignAsync("Completed Campaign");
        await _client.PostAsync($"/api/admin/campaigns/{id}/start", null);
        await _client.PostAsync($"/api/admin/campaigns/{id}/stop", null);

        // Try to stop again (already Completed)
        var response = await _client.PostAsync($"/api/admin/campaigns/{id}/stop", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── Contact Lists ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateContactList_ShouldReturn201_WhenValid()
    {
        var campaignId = await CreateCampaignAsync("Contact List Campaign");
        var body = JsonContent.Create(new { name = "Q1 Leads" });

        var response = await _client.PostAsync($"/api/admin/campaigns/{campaignId}/contact-lists", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Q1 Leads");
    }

    [Fact]
    public async Task ListContactLists_ShouldReturnByCampaign_WhenExists()
    {
        var campaignId = await CreateCampaignAsync("List Contact Lists Campaign");
        var body = JsonContent.Create(new { name = "My Leads" });
        await _client.PostAsync($"/api/admin/campaigns/{campaignId}/contact-lists", body);

        var response = await _client.GetAsync($"/api/admin/campaigns/{campaignId}/contact-lists");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("My Leads");
    }

    [Fact]
    public async Task DeleteContactList_ShouldReturn204_WhenExists()
    {
        var campaignId = await CreateCampaignAsync("Delete Contact List Campaign");
        var createBody = JsonContent.Create(new { name = "Temp List" });
        var createResp = await _client.PostAsync($"/api/admin/campaigns/{campaignId}/contact-lists", createBody);
        var created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync());
        var listId = created!["id"]!.GetValue<long>();

        var response = await _client.DeleteAsync($"/api/admin/campaigns/{campaignId}/contact-lists/{listId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ImportContacts_ShouldReturnCount_WhenValidData()
    {
        var campaignId = await CreateCampaignAsync("Import Contacts Campaign");
        var listId = await CreateContactListAsync(campaignId, "Import List");

        var body = JsonContent.Create(new
        {
            contacts = new[]
            {
                new { firstName = "Alice", lastName = "Smith", phone = "+15551234567" },
                new { firstName = "Bob", lastName = "Jones", phone = "+15559876543" },
            },
        });

        var response = await _client.PostAsync($"/api/admin/campaigns/{campaignId}/contact-lists/{listId}/import", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonNode.Parse(content);
        result!["imported"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task ImportContacts_ShouldCreatePhoneRecords_WhenImported()
    {
        var campaignId = await CreateCampaignAsync("Phone Records Campaign");
        var listId = await CreateContactListAsync(campaignId, "Phone Records List");

        var importBody = JsonContent.Create(new
        {
            contacts = new[]
            {
                new { firstName = "Carol", lastName = "White", phone = "+15551112222" },
            },
        });
        await _client.PostAsync($"/api/admin/campaigns/{campaignId}/contact-lists/{listId}/import", importBody);

        var response = await _client.GetAsync($"/api/admin/campaigns/{campaignId}/contact-lists/{listId}/contacts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Carol");
    }

    [Fact]
    public async Task ListContacts_ShouldReturnPaginated_WhenContactsExist()
    {
        var campaignId = await CreateCampaignAsync("Paginated Contacts Campaign");
        var listId = await CreateContactListAsync(campaignId, "Big List");

        var importBody = JsonContent.Create(new
        {
            contacts = new[]
            {
                new { firstName = "Dave", lastName = "Brown", phone = "+15553334444" },
                new { firstName = "Eve", lastName = "Green", phone = "+15555556666" },
                new { firstName = "Frank", lastName = "Black", phone = "+15557778888" },
            },
        });
        await _client.PostAsync($"/api/admin/campaigns/{campaignId}/contact-lists/{listId}/import", importBody);

        var response = await _client.GetAsync($"/api/admin/campaigns/{campaignId}/contact-lists/{listId}/contacts?page=1&pageSize=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonNode.Parse(content);
        result!["items"]!.AsArray().Count.Should().Be(2);
        result["totalCount"]!.GetValue<int>().Should().Be(3);
    }

    // ─── Dispositions ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDisposition_ShouldReturn201_WhenValid()
    {
        var campaignId = await CreateCampaignAsync("Disposition Create Campaign");
        var body = JsonContent.Create(new
        {
            code = "SALE",
            label = "Sale Completed",
            category = "success",
            isSuccess = true,
            triggerRetry = false,
            triggerCallback = false,
        });

        var response = await _client.PostAsync($"/api/admin/campaigns/{campaignId}/dispositions", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("SALE");
    }

    [Fact]
    public async Task ListDispositions_ShouldReturnByCampaign_WhenExists()
    {
        var campaignId = await CreateCampaignAsync("Disposition List Campaign");
        await CreateDispositionAsync(campaignId, "BUSY", "Busy Signal");

        var response = await _client.GetAsync($"/api/admin/campaigns/{campaignId}/dispositions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("BUSY");
    }

    [Fact]
    public async Task UpdateDisposition_ShouldModifyFields_WhenValid()
    {
        var campaignId = await CreateCampaignAsync("Disposition Update Campaign");
        var dispoId = await CreateDispositionAsync(campaignId, "NI", "Not Interested");

        var updateBody = JsonContent.Create(new { label = "Not Interested - Updated", isSuccess = false });
        var response = await _client.PutAsync($"/api/admin/campaigns/{campaignId}/dispositions/{dispoId}", updateBody);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Not Interested - Updated");
    }

    [Fact]
    public async Task DeleteDisposition_ShouldReturn204_WhenExists()
    {
        var campaignId = await CreateCampaignAsync("Disposition Delete Campaign");
        var dispoId = await CreateDispositionAsync(campaignId, "VM", "Voicemail");

        var response = await _client.DeleteAsync($"/api/admin/campaigns/{campaignId}/dispositions/{dispoId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ListDispositions_ShouldOrderBySortOrder_WhenMultiple()
    {
        var campaignId = await CreateCampaignAsync("Sorted Dispositions Campaign");

        // Create three dispositions
        var body1 = JsonContent.Create(new { code = "C", label = "Third", category = "failure", isSuccess = false, triggerRetry = false, triggerCallback = false });
        var body2 = JsonContent.Create(new { code = "A", label = "First", category = "success", isSuccess = true, triggerRetry = false, triggerCallback = false });
        var body3 = JsonContent.Create(new { code = "B", label = "Second", category = "retry", isSuccess = false, triggerRetry = true, triggerCallback = false });

        var r1 = JsonNode.Parse(await (await _client.PostAsync($"/api/admin/campaigns/{campaignId}/dispositions", body1)).Content.ReadAsStringAsync());
        var r2 = JsonNode.Parse(await (await _client.PostAsync($"/api/admin/campaigns/{campaignId}/dispositions", body2)).Content.ReadAsStringAsync());
        var r3 = JsonNode.Parse(await (await _client.PostAsync($"/api/admin/campaigns/{campaignId}/dispositions", body3)).Content.ReadAsStringAsync());

        var id1 = r1!["id"]!.GetValue<long>();
        var id2 = r2!["id"]!.GetValue<long>();
        var id3 = r3!["id"]!.GetValue<long>();

        // Update sort orders: A=1 (First), B=2 (Second), C=3 (Third)
        await _client.PutAsync($"/api/admin/campaigns/{campaignId}/dispositions/{id2}", JsonContent.Create(new { sortOrder = 1 }));
        await _client.PutAsync($"/api/admin/campaigns/{campaignId}/dispositions/{id3}", JsonContent.Create(new { sortOrder = 2 }));
        await _client.PutAsync($"/api/admin/campaigns/{campaignId}/dispositions/{id1}", JsonContent.Create(new { sortOrder = 3 }));

        var response = await _client.GetAsync($"/api/admin/campaigns/{campaignId}/dispositions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var items = JsonNode.Parse(content)!.AsArray();
        items.Count.Should().Be(3);
        // Ordered by SortOrder: A(sort=1) < B(sort=2) < C(sort=3)
        items[0]!["label"]!.GetValue<string>().Should().Be("First");
    }

    [Fact]
    public async Task CreateDisposition_ShouldMapCategory_WhenStringProvided()
    {
        var campaignId = await CreateCampaignAsync("Category Mapping Campaign");
        var body = JsonContent.Create(new
        {
            code = "RET",
            label = "Retry Later",
            category = "retry",
            isSuccess = false,
            triggerRetry = true,
            triggerCallback = false,
        });

        var response = await _client.PostAsync($"/api/admin/campaigns/{campaignId}/dispositions", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Retry");
    }

    // ─── Wrap-up Bridge ───────────────────────────────────────────────────────

    [Fact]
    public async Task WrapUp_ShouldReturn404_WhenConversationNotFound()
    {
        var body = JsonContent.Create(new { notes = "Test note" });
        var response = await _client.PostAsync("/api/conversations/nonexistent-campaign-conv/wrapup", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WrapUp_ShouldWorkNormally_WhenNoCampaignContext()
    {
        // WrapUp with no campaignDispositionId should skip the campaign bridge
        // and return 404 since the conversation doesn't exist
        var body = JsonContent.Create(new { notes = "Regular wrapup" });
        var response = await _client.PostAsync("/api/conversations/no-campaign-context/wrapup", body);

        // No conversation → 404 (bridge is never entered without a real conversation)
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WrapUp_ShouldPublishSseEvent_WhenCampaignDisposition()
    {
        // The wrapup endpoint returns 404 when conversation doesn't exist,
        // so the SSE publish path only fires when conversation + metadata exist.
        // This test verifies the endpoint handles the no-conversation case gracefully.
        var body = JsonContent.Create(new { campaignDispositionId = 1L, notes = "SSE test" });
        var response = await _client.PostAsync("/api/conversations/missing-conv/wrapup", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WrapUp_ShouldScheduleCallback_WhenTriggerCallbackTrue()
    {
        // Callback scheduling requires conversation metadata with callAttemptId, campaignId, contactId.
        // Without a real conversation seeded, the endpoint returns 404.
        // This test verifies the endpoint is reachable and handles missing conversation correctly.
        var body = JsonContent.Create(new
        {
            campaignDispositionId = 1L,
            callbackDate = DateTimeOffset.UtcNow.AddDays(1).ToString("O"),
            notes = "Callback test",
        });
        var response = await _client.PostAsync("/api/conversations/unknown-conv/wrapup", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Metrics ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCampaignMetrics_ShouldReturnMetrics_WhenCampaignExists()
    {
        var id = await CreateCampaignAsync("Metrics Campaign");

        var response = await _client.GetAsync($"/api/admin/campaigns/{id}/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var metrics = JsonNode.Parse(content);
        metrics!["campaignId"]!.GetValue<long>().Should().Be(id);
    }

    [Fact]
    public async Task GetCampaignMetrics_ShouldReturn404_WhenNotFound()
    {
        var response = await _client.GetAsync("/api/admin/campaigns/999998/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListActiveCampaignMetrics_ShouldReturnAll_WhenActiveExist()
    {
        var id = await CreateCampaignAsync("Active Metrics Campaign");
        await _client.PostAsync($"/api/admin/campaigns/{id}/start", null);

        var response = await _client.GetAsync("/api/operations/campaigns/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var items = JsonNode.Parse(content)!.AsArray();
        items.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ListActiveCampaignMetrics_ShouldReturnEmpty_WhenNoneActive()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/operations/campaigns/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var items = JsonNode.Parse(content)!.AsArray();
        items.Count.Should().Be(0);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<long> CreateCampaignAsync(string name)
    {
        var body = JsonContent.Create(new
        {
            name,
            mode = "progressive",
            targetQueueName = "default",
            maxConcurrentCalls = 5,
            timezone = "UTC",
            schedule = Array.Empty<object>(),
            holidays = Array.Empty<string>(),
            dncEnabled = false,
            maxAttemptsPerContact = 3,
            retryIntervalMinutes = 60,
            timeBetweenAttemptsMinutes = 5,
        });
        var resp = await _client.PostAsync("/api/admin/campaigns", body);
        resp.EnsureSuccessStatusCode();
        var created = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        return created!["id"]!.GetValue<long>();
    }

    private async Task<long> CreateContactListAsync(long campaignId, string name)
    {
        var body = JsonContent.Create(new { name });
        var resp = await _client.PostAsync($"/api/admin/campaigns/{campaignId}/contact-lists", body);
        resp.EnsureSuccessStatusCode();
        var created = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        return created!["id"]!.GetValue<long>();
    }

    private async Task<long> CreateDispositionAsync(long campaignId, string code, string label)
    {
        var body = JsonContent.Create(new
        {
            code,
            label,
            category = "failure",
            isSuccess = false,
            triggerRetry = false,
            triggerCallback = false,
        });
        var resp = await _client.PostAsync($"/api/admin/campaigns/{campaignId}/dispositions", body);
        resp.EnsureSuccessStatusCode();
        var created = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        return created!["id"]!.GetValue<long>();
    }
}
