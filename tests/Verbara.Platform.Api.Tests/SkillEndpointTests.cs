using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests;

public sealed class SkillEndpointTests : IClassFixture<UnifiedPlatformApiFactory>
{
    private readonly UnifiedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public SkillEndpointTests(UnifiedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task CreateSkill_ShouldReturn201()
    {
        var name = $"test-{Guid.NewGuid():N}";
        var body = JsonContent.Create(new { name, category = "technical", description = "A test skill" });

        var response = await _client.PostAsync("/api/admin/skills", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ListSkills_ShouldReturn200()
    {
        var response = await _client.GetAsync("/api/admin/skills");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteSkill_ShouldReturn204_WhenNoAgentsAssigned()
    {
        var name = $"test-{Guid.NewGuid():N}";
        await CreateSkillAsync(name);

        var response = await _client.DeleteAsync($"/api/admin/skills/{name}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteSkill_ShouldReturn409_WhenAgentsAssignedWithoutForce()
    {
        var skillName = $"test-{Guid.NewGuid():N}";
        await CreateSkillAsync(skillName);

        var agentId = $"agent-{Guid.NewGuid():N}";
        await AssignSkillToAgentAsync(agentId, skillName);

        var response = await _client.DeleteAsync($"/api/admin/skills/{skillName}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteSkill_ShouldReturn204_WhenAgentsAssignedWithForce()
    {
        var skillName = $"test-{Guid.NewGuid():N}";
        await CreateSkillAsync(skillName);

        var agentId = $"agent-{Guid.NewGuid():N}";
        await AssignSkillToAgentAsync(agentId, skillName);

        var response = await _client.DeleteAsync($"/api/admin/skills/{skillName}?force=true");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ListAgentsWithSkill_ShouldReturn200_WithEmptyList()
    {
        var skillName = $"test-{Guid.NewGuid():N}";
        await CreateSkillAsync(skillName);

        var response = await _client.GetAsync($"/api/admin/skills/{skillName}/agents");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var items = JsonNode.Parse(content)!.AsArray();
        items.Count.Should().Be(0);
    }

    [Fact]
    public async Task ListAgentsWithSkill_ShouldReturnAssignedAgents()
    {
        var skillName = $"test-{Guid.NewGuid():N}";
        await CreateSkillAsync(skillName);

        var agentId1 = $"agent-{Guid.NewGuid():N}";
        var agentId2 = $"agent-{Guid.NewGuid():N}";
        await AssignSkillToAgentAsync(agentId1, skillName);
        await AssignSkillToAgentAsync(agentId2, skillName);

        var response = await _client.GetAsync($"/api/admin/skills/{skillName}/agents");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var items = JsonNode.Parse(content)!.AsArray();
        items.Count.Should().Be(2);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task CreateSkillAsync(string name, string? category = "technical", string? description = null)
    {
        var body = JsonContent.Create(new { name, category, description });
        var resp = await _client.PostAsync("/api/admin/skills", body);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Assigns the skill to an agent of the caller's tenant, seeding the agent first (the route answers 404 for
    /// an agent the tenant does not have).</summary>
    private async Task AssignSkillToAgentAsync(string agentId, string skillName, int proficiency = 5)
    {
        await _factory.AgentStore.SaveAsync(new Agent
        {
            AgentId = EntityId.From(agentId),
            TenantId = new TenantId(UnifiedPlatformApiFactory.TestTenantId),
            UserId = EntityId.From($"user-{agentId}"),
            DisplayName = $"Agent {agentId}",
            State = AgentState.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        var body = JsonContent.Create(new { skillName, proficiency });
        var resp = await _client.PostAsync($"/api/admin/agents/{agentId}/skills", body);
        resp.EnsureSuccessStatusCode();
    }
}
