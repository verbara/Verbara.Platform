using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests;

public sealed class AdminEndpointTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public AdminEndpointTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // ─── Queues ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListQueues_ShouldReturn200_WithEmptyResult()
    {
        var response = await _client.GetAsync("/api/admin/queues");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateQueue_ShouldReturn201_WithCreatedQueue()
    {
        var body = JsonContent.Create(new { name = "Support Queue" });
        var response = await _client.PostAsync("/api/admin/queues", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Support Queue");
    }

    [Fact]
    public async Task GetQueue_ShouldReturn404_WhenQueueDoesNotExist()
    {
        var response = await _client.GetAsync("/api/admin/queues/nonexistent-queue-id");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateAndGetQueue_ShouldReturn200_WhenQueueExists()
    {
        var body = JsonContent.Create(new { name = "Sales Queue" });
        var createResponse = await _client.PostAsync("/api/admin/queues", body);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // QueueDto emits `id` (Plan 29A DTO-hardening convention), not the aggregate's `queueId`.
        var created = JsonNode.Parse(await createResponse.Content.ReadAsStringAsync());
        var queueId = created!["id"]!.GetValue<string>();

        var getResponse = await _client.GetAsync($"/api/admin/queues/{queueId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await getResponse.Content.ReadAsStringAsync();
        content.Should().Contain("Sales Queue");
    }

    [Fact]
    public async Task UpdateQueue_ShouldReturn404_WhenQueueDoesNotExist()
    {
        var body = JsonContent.Create(new { name = "Updated Name" });
        var response = await _client.PutAsync("/api/admin/queues/no-such-queue", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteQueue_ShouldReturn204()
    {
        // Create first
        var createBody = JsonContent.Create(new { name = "Queue To Delete" });
        var createResp = await _client.PostAsync("/api/admin/queues", createBody);
        var created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync());
        var queueId = created!["id"]!.GetValue<string>();

        var response = await _client.DeleteAsync($"/api/admin/queues/{queueId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─── Users ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListUsers_ShouldReturn200_WithEmptyResult()
    {
        var response = await _client.GetAsync("/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateUser_ShouldReturn201_WithCreatedUser()
    {
        var body = JsonContent.Create(new { email = "agent@example.com", displayName = "Test Agent", role = 0 });
        var response = await _client.PostAsync("/api/admin/users", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("agent@example.com");
    }

    [Fact]
    public async Task GetUser_ShouldReturn404_WhenUserDoesNotExist()
    {
        var response = await _client.GetAsync("/api/admin/users/no-such-user");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteUser_ShouldReturn204()
    {
        var createBody = JsonContent.Create(new { email = "delete-me@example.com", displayName = "To Delete", role = 0 });
        var createResp = await _client.PostAsync("/api/admin/users", createBody);
        var created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync());
        var userId = created!["id"]!.GetValue<string>();

        var response = await _client.DeleteAsync($"/api/admin/users/{userId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─── v1.14.3 — R5.5 P0 finding #4 + #5 regression coverage ────────────────

    [Fact]
    public async Task CreateUser_ShouldReturn409_WhenEmailAlreadyExists()
    {
        var firstBody = JsonContent.Create(new { email = "duplicate@example.com", displayName = "First", role = 0 });
        var firstResp = await _client.PostAsync("/api/admin/users", firstBody);
        firstResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // Same email, different display name → must collide on idx_users_email.
        var dupBody = JsonContent.Create(new { email = "duplicate@example.com", displayName = "Second", role = 0 });
        var dupResp = await _client.PostAsync("/api/admin/users", dupBody);

        dupResp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Body should be RFC-7807 problem details with our stable type URI.
        var problem = JsonNode.Parse(await dupResp.Content.ReadAsStringAsync());
        problem!["status"]!.GetValue<int>().Should().Be(409);
        problem["type"]!.GetValue<string>().Should().Be("https://verbara.platform/errors/entity-already-exists");
    }

    [Fact]
    public async Task ListUsers_ShouldFilterByEmail_WhenQueryParamSupplied()
    {
        // Seed two users with distinguishable emails.
        await _client.PostAsync(
            "/api/admin/users",
            JsonContent.Create(new { email = "alice@filter.test", displayName = "Alice", role = 0 }));
        await _client.PostAsync(
            "/api/admin/users",
            JsonContent.Create(new { email = "bob@other.test", displayName = "Bob", role = 0 }));

        // Substring match on `alice` should return ONLY alice@filter.test.
        var response = await _client.GetAsync("/api/admin/users?email=alice");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var items = page!["items"]!.AsArray();
        items.Count.Should().Be(1);
        items[0]!["email"]!.GetValue<string>().Should().Be("alice@filter.test");
    }

    [Fact]
    public async Task ListUsers_ShouldFilterCaseInsensitively_WhenQueryParamSupplied()
    {
        await _client.PostAsync(
            "/api/admin/users",
            JsonContent.Create(new { email = "Charlie@MIXED.test", displayName = "Charlie", role = 0 }));

        // Lowercase needle against mixed-case stored email.
        var response = await _client.GetAsync("/api/admin/users?email=charlie");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        page!["items"]!.AsArray().Count.Should().Be(1);
    }

    // ─── Agents ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAgents_ShouldReturn200_WithEmptyResult()
    {
        var response = await _client.GetAsync("/api/admin/agents");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateAgent_ShouldReturn201_WithCreatedAgent()
    {
        var body = JsonContent.Create(new { userId = "user-xyz-001", displayName = "Alice Agent" });
        var response = await _client.PostAsync("/api/admin/agents", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Alice Agent");
    }

    [Fact]
    public async Task GetAgent_ShouldReturn404_WhenAgentDoesNotExist()
    {
        var response = await _client.GetAsync("/api/admin/agents/no-such-agent");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── W6-A6 — per-agent capacity override on create/update + effective in responses ──

    [Fact]
    public async Task UpdateAgent_ShouldRejectMaxVoiceAboveOne_WhenOverrideSet()
    {
        var agentId = await CreateAgentAsync("Voice Over Cap");

        var response = await _client.PutAsync(
            $"/api/v1/admin/agents/{agentId}",
            JsonContent.Create(new { capacity = new { maxVoice = 2 } }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateAgent_ShouldRejectNegativeCapacity_WhenOverrideSet()
    {
        var agentId = await CreateAgentAsync("Negative Cap");

        var response = await _client.PutAsync(
            $"/api/v1/admin/agents/{agentId}",
            JsonContent.Create(new { capacity = new { maxChat = -1 } }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateAgent_ShouldRejectCapacityAboveMax_WhenOverrideSet()
    {
        var agentId = await CreateAgentAsync("Absurd Cap");

        var response = await _client.PutAsync(
            $"/api/v1/admin/agents/{agentId}",
            JsonContent.Create(new { capacity = new { maxEmail = 9999 } }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateAgent_ShouldPersistCapacityOverride_WhenValid()
    {
        var agentId = await CreateAgentAsync("Persist Cap");

        var response = await _client.PutAsync(
            $"/api/v1/admin/agents/{agentId}",
            JsonContent.Create(new { capacity = new { maxChat = 7, maxTotal = 9 } }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        dto["capacityOverride"]!["maxChat"]!.GetValue<int>().Should().Be(7);
        dto["capacityOverride"]!["maxTotal"]!.GetValue<int>().Should().Be(9);
        // Effective merges over the tenant default (chat overridden to 7) and pins voice.
        dto["effectiveCapacity"]!["maxChat"]!.GetValue<int>().Should().Be(7);
        dto["effectiveCapacity"]!["maxVoice"]!.GetValue<int>().Should().Be(1);

        // Re-fetch confirms durability.
        var get = await _client.GetAsync($"/api/v1/admin/agents/{agentId}");
        var getDto = JsonNode.Parse(await get.Content.ReadAsStringAsync())!;
        getDto["capacityOverride"]!["maxChat"]!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public async Task UpdateAgent_ShouldEmitCapacityAudit_WhenOverrideChanged()
    {
        var agentId = await CreateAgentAsync("Audited Cap");

        var response = await _client.PutAsync(
            $"/api/v1/admin/agents/{agentId}",
            JsonContent.Create(new { capacity = new { maxChat = 4 } }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var auditStore = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var hits = await auditStore.SearchAsync(
            new TenantId(AuthenticatedPlatformApiFactory.TestTenantId),
            new AuditQuery(Action: "agent.capacity_override", Page: 1, PageSize: 100),
            CancellationToken.None);

        hits.Items.Should().ContainSingle(e =>
            e.TargetId == agentId
            && e.Category == "queues"
            && e.ActorType == "user"
            && e.Metadata != null
            && e.Metadata["new_max_chat"] == "4"
            && e.Metadata["old_max_chat"] == "inherit");
    }

    [Fact]
    public async Task GetAgent_ShouldReturnEffectiveAndNullOverride_WhenInheriting()
    {
        var agentId = await CreateAgentAsync("Inheriting Cap");

        var response = await _client.GetAsync($"/api/v1/admin/agents/{agentId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        // Fully inherited → no raw override emitted (UI renders "inherited").
        dto["capacityOverride"].Should().BeNull();
        // Effective reflects the tenant defaults (1/3/5/3/5 for an unconfigured test tenant).
        dto["effectiveCapacity"]!["maxVoice"]!.GetValue<int>().Should().Be(1);
        dto["effectiveCapacity"]!["maxChat"]!.GetValue<int>().Should().Be(3);
        dto["effectiveCapacity"]!["maxEmail"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public async Task GetAgent_ShouldReturnOverride_WhenSet()
    {
        var agentId = await CreateAgentAsync("With Override");
        await _client.PutAsync(
            $"/api/v1/admin/agents/{agentId}",
            JsonContent.Create(new { capacity = new { maxSms = 2 } }));

        var response = await _client.GetAsync($"/api/v1/admin/agents/{agentId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        dto["capacityOverride"].Should().NotBeNull();
        dto["capacityOverride"]!["maxSms"]!.GetValue<int>().Should().Be(2);
        // The non-overridden fields stay null in the raw override DTO.
        dto["capacityOverride"]!["maxChat"].Should().BeNull();
        dto["effectiveCapacity"]!["maxSms"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task CreateAgent_ShouldPersistCapacityOverride_WhenSupplied()
    {
        var userId = $"user-{Guid.NewGuid():N}";
        var response = await _client.PostAsync(
            "/api/v1/admin/agents",
            JsonContent.Create(new { userId, displayName = "Created With Cap", capacity = new { maxChat = 6 } }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        dto["capacityOverride"]!["maxChat"]!.GetValue<int>().Should().Be(6);
        dto["effectiveCapacity"]!["maxChat"]!.GetValue<int>().Should().Be(6);
    }

    // ─── ADR-0026 Phase A.6 — agent-centric membership listing ────────────────

    [Fact]
    public async Task ListAgentQueueMemberships_ShouldReturn404_WhenAgentDoesNotExist()
    {
        var response = await _client.GetAsync(
            "/api/admin/agents/no-such-agent/queue-memberships");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListAgentQueueMemberships_ShouldReturn200_WithEmptyList_WhenAgentHasNoMemberships()
    {
        var agentId = await CreateAgentAsync("Sin Memberships");

        var response = await _client.GetAsync(
            $"/api/admin/agents/{agentId}/queue-memberships");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var arr = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        arr.Count.Should().Be(0);
    }

    [Fact]
    public async Task ListAgentQueueMemberships_ShouldReturn200_WithJoinedQueueName_WhenMembershipExists()
    {
        var queueId = await CreateQueueAsync("Agent-Membership Queue");
        var agentId = await CreateAgentAsync("Con Membership");
        await AddMemberAsync(queueId, agentId);

        var response = await _client.GetAsync(
            $"/api/admin/agents/{agentId}/queue-memberships");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var arr = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        arr.Count.Should().Be(1);
        var dto = arr[0]!;
        dto["queueId"]!.GetValue<string>().Should().Be(queueId);
        dto["queueName"]!.GetValue<string>().Should().Be("Agent-Membership Queue");
        dto["source"]!.GetValue<string>().Should().Be("Manual");
    }

    [Fact]
    public async Task ListAgentQueueMemberships_ShouldExposeAllowedChannels_WhenRestricted()
    {
        var queueId = await CreateQueueAsync("Restricted-Channels Queue");
        var agentId = await CreateAgentAsync("Restricted Member");
        // Restrict to WebChat-only at creation.
        await _client.PostAsync(
            $"/api/v1/queues/{queueId}/members",
            JsonContent.Create(new { agentId, allowedChannels = ChannelTestData.WebChatOnly }));

        var response = await _client.GetAsync(
            $"/api/admin/agents/{agentId}/queue-memberships");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()[0]!;
        var channels = dto["allowedChannels"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        channels.Should().BeEquivalentTo(["WebChat"]);
    }

    // Local helpers — duplicate the small builders from QueueMembersEndpointsTests
    // to keep AdminEndpointTests self-contained (no shared test fixture needed).
    private async Task<string> CreateQueueAsync(string name)
    {
        var response = await _client.PostAsync(
            "/api/v1/admin/queues",
            JsonContent.Create(new { name }));
        response.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return node!["id"]!.GetValue<string>();
    }

    private async Task<string> CreateAgentAsync(string displayName)
    {
        var userId = $"user-{Guid.NewGuid():N}";
        var response = await _client.PostAsync(
            "/api/v1/admin/agents",
            JsonContent.Create(new { userId, displayName }));
        response.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return node!["agentId"]!.GetValue<string>();
    }

    private async Task AddMemberAsync(string queueId, string agentId)
    {
        var response = await _client.PostAsync(
            $"/api/v1/queues/{queueId}/members",
            JsonContent.Create(new { agentId }));
        response.EnsureSuccessStatusCode();
    }

    // ─── Teams ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTeams_ShouldReturn200_WithEmptyResult()
    {
        var response = await _client.GetAsync("/api/admin/teams");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateTeam_ShouldReturn201_WithCreatedTeam()
    {
        var body = JsonContent.Create(new { name = "Alpha Team" });
        var response = await _client.PostAsync("/api/admin/teams", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Alpha Team");
    }

    [Fact]
    public async Task DeleteTeam_ShouldReturn204()
    {
        var createBody = JsonContent.Create(new { name = "Temp Team" });
        var createResp = await _client.PostAsync("/api/admin/teams", createBody);
        var created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync());
        var teamId = created!["id"]!.GetValue<string>();

        var response = await _client.DeleteAsync($"/api/admin/teams/{teamId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
