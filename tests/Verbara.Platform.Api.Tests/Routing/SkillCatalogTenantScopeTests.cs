using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Dialer;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests.Routing;

/// <summary>
/// routing-skills-stay-in-their-tenant: the skill-definition routes, the agent skill routes, their audit entries and
/// the queue penalty they feed act only on the caller's tenant, through the HTTP pipeline with two Customer tenants
/// (<c>acme</c>, <c>globex</c>) authenticated by real JWTs (<see cref="DialerTenantScopeFactory"/>). Agents are seeded
/// through the factory's in-memory <see cref="IAgentStore"/>; skills through the tenant's own HTTP calls. Every test
/// uses its own agent ids and skill names, because the fixture's host is shared by the class.
/// </summary>
public sealed class SkillCatalogTenantScopeTests : IClassFixture<DialerTenantScopeFactory>
{
    private const string SkillsPath = "/api/admin/skills";
    private const string AgentsPath = "/api/admin/agents";
    private const string Acme = DialerTenantScopeFactory.AcmeTenantId;
    private const string Globex = DialerTenantScopeFactory.GlobexTenantId;

    private readonly DialerTenantScopeFactory _factory;

    public SkillCatalogTenantScopeTests(DialerTenantScopeFactory factory)
    {
        _factory = factory;
    }

    // ─── Skill definitions belong to one tenant ──────────────────────────────────

    [Fact]
    public async Task ListSkills_ShouldNotCarryTheSkill_WhenAnotherTenantDefinesIt()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var skill = UniqueSkill("billing");
        await CreateSkillAsync(acme, skill, "product");

        using var response = await globex.GetAsync(SkillsPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SkillNamesAsync(response)).Should().NotContain(skill,
            "skill {0} is defined by acme, so it is no skill of globex's", skill);
    }

    [Fact]
    public async Task UpsertSkill_ShouldLeaveTheOtherTenantsSkillUnchanged_WhenBothTenantsUseTheSameName()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var skill = UniqueSkill("billing");
        await CreateSkillAsync(acme, skill, "product");

        using var response = await globex.PutAsync($"{SkillsPath}/{skill}",
            JsonContent.Create(new { category = "finance", description = default(string) }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CategoryOfAsync(globex, skill)).Should().Be("finance");
        (await CategoryOfAsync(acme, skill)).Should().Be("product",
            "globex's {0} is a different skill from acme's {0}", skill);
    }

    // ─── A skill the tenant does not define cannot be deleted ────────────────────

    [Fact]
    public async Task DeleteSkill_ShouldReturn404AndLeaveTheSkillAndItsHolder_WhenAnotherTenantDefinesIt()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var skill = UniqueSkill("billing");
        var agentId = await SeedAgentAsync(Acme);
        await CreateSkillAsync(acme, skill, "product");
        await AssignAsync(acme, agentId, skill, 8);

        using var response = await globex.DeleteAsync($"{SkillsPath}/{skill}?force=true");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "skill {0} is defined by acme only, so globex defines no skill of that name", skill);
        using var list = await acme.GetAsync(SkillsPath);
        (await SkillNamesAsync(list)).Should().Contain(skill);
        (await AgentSkillsAsync(acme, agentId)).Should().ContainKey(skill);
    }

    [Fact]
    public async Task DeleteSkill_ShouldReturn404AndRemoveNoAssignment_WhenTheTenantDefinesNoSkillOfThatName()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var skill = UniqueSkill("spanish");
        var agentId = await SeedAgentAsync(Acme);
        await AssignAsync(acme, agentId, skill, 6);

        using var withoutForce = await acme.DeleteAsync($"{SkillsPath}/{skill}");
        var heldAfterPlainDelete = await AgentSkillsAsync(acme, agentId);
        using var withForce = await acme.DeleteAsync($"{SkillsPath}/{skill}?force=true");
        var heldAfterForcedDelete = await AgentSkillsAsync(acme, agentId);
        var deleted = await AuditEntriesAsync(Acme, "skill.deleted", skill);

        using (new AssertionScope())
        {
            withoutForce.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "acme defines no skill {0}, whoever holds the name", skill);
            heldAfterPlainDelete.Should().ContainKey(skill);
            withForce.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "acme defines no skill {0}, with or without force", skill);
            heldAfterForcedDelete.Should().ContainKey(skill,
                "a delete answered 404 removes no assignment of {0}", skill);
            deleted.Should().BeEmpty("a delete answered 404 records nothing");
        }
    }

    // ─── The agents of a skill are the tenant's own ──────────────────────────────

    [Fact]
    public async Task ListAgentsWithSkill_ShouldListOnlyTheTenantsAgents_WhenBothTenantsDefineTheName()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var skill = UniqueSkill("billing");
        var acmeAgent = await SeedAgentAsync(Acme);
        var globexAgent = await SeedAgentAsync(Globex);
        await CreateSkillAsync(acme, skill, "product");
        await CreateSkillAsync(globex, skill, "finance");
        await AssignAsync(acme, acmeAgent, skill, 8);
        await AssignAsync(globex, globexAgent, skill, 4);

        using var response = await globex.GetAsync($"{SkillsPath}/{skill}/agents");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var agentIds = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
            .Select(n => n!["agentId"]!.GetValue<string>()).ToList();
        agentIds.Should().Contain(globexAgent);
        agentIds.Should().NotContain(acmeAgent, "agent {0} is acme's, not globex's", acmeAgent);
    }

    [Fact]
    public async Task DeleteSkill_ShouldReturn204AndLeaveTheOtherTenantsSkill_WhenOnlyAnotherTenantsAgentHoldsIt()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var skill = UniqueSkill("billing");
        var acmeAgent = await SeedAgentAsync(Acme);
        await CreateSkillAsync(acme, skill, "product");
        await CreateSkillAsync(globex, skill, "finance");
        await AssignAsync(acme, acmeAgent, skill, 8);

        using var response = await globex.DeleteAsync($"{SkillsPath}/{skill}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "no agent of globex holds {0}; acme's agent {1} is not counted", skill, acmeAgent);
        using var list = await acme.GetAsync(SkillsPath);
        (await SkillNamesAsync(list)).Should().Contain(skill);
        using var holders = await acme.GetAsync($"{SkillsPath}/{skill}/agents");
        JsonNode.Parse(await holders.Content.ReadAsStringAsync())!.AsArray()
            .Select(n => n!["agentId"]!.GetValue<string>()).Should().Contain(acmeAgent);
    }

    // ─── Agent skill routes act only on an agent of the caller's tenant ──────────

    [Fact]
    public async Task GetAgentSkills_ShouldReturn404AndCarryNoSkill_WhenTheAgentBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var skill = UniqueSkill("billing");
        var agentId = await SeedAgentAsync(Acme);
        await AssignAsync(acme, agentId, skill, 8);

        using var response = await globex.GetAsync($"{AgentsPath}/{agentId}/skills");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "agent {0} belongs to acme, so it is no agent of globex's", agentId);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(skill);
    }

    [Fact]
    public async Task AssignSkill_ShouldReturn404AndLeaveTheProficiency_WhenTheAgentBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var skill = UniqueSkill("billing");
        var agentId = await SeedAgentAsync(Acme);
        await AssignAsync(acme, agentId, skill, 8);

        using var response = await globex.PostAsync($"{AgentsPath}/{agentId}/skills",
            JsonContent.Create(new { skillName = skill, proficiency = 1 }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "agent {0} belongs to acme, so it is no agent of globex's", agentId);
        (await AgentSkillsAsync(acme, agentId)).Should().Contain(skill, 8);
    }

    [Fact]
    public async Task RemoveAgentSkill_ShouldReturn404AndLeaveTheAssignment_WhenTheAgentBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var skill = UniqueSkill("billing");
        var agentId = await SeedAgentAsync(Acme);
        await AssignAsync(acme, agentId, skill, 8);

        using var response = await globex.DeleteAsync($"{AgentsPath}/{agentId}/skills/{skill}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "agent {0} belongs to acme, so it is no agent of globex's", agentId);
        (await AgentSkillsAsync(acme, agentId)).Should().ContainKey(skill);
    }

    [Fact]
    public async Task AssignSkill_ShouldReturn404AndStoreNothing_WhenNoTenantHasTheAgent()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var skill = UniqueSkill("billing");
        var ghost = $"ghost-{Guid.NewGuid():N}";

        using var response = await acme.PostAsync($"{AgentsPath}/{ghost}/skills",
            JsonContent.Create(new { skillName = skill, proficiency = 7 }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "no tenant has an agent {0}", ghost);
        using var holders = await acme.GetAsync($"{SkillsPath}/{skill}/agents");
        holders.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await holders.Content.ReadAsStringAsync())!.AsArray().Should().BeEmpty(
            "nothing is stored for an unknown agent");
    }

    [Fact]
    public async Task AgentSkillRoutes_ShouldAnswerAsBefore_WhenTheAgentBelongsToTheTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var skill = UniqueSkill("billing");
        var agentId = await SeedAgentAsync(Acme);

        using var assign = await acme.PostAsync($"{AgentsPath}/{agentId}/skills",
            JsonContent.Create(new { skillName = skill, proficiency = 7 }));
        assign.StatusCode.Should().Be(HttpStatusCode.Created);
        var assigned = JsonNode.Parse(await assign.Content.ReadAsStringAsync())!;
        assigned["agentId"]!.GetValue<string>().Should().Be(agentId);
        assigned["skillName"]!.GetValue<string>().Should().Be(skill);
        assigned["proficiency"]!.GetValue<int>().Should().Be(7);

        using var list = await acme.GetAsync($"{AgentsPath}/{agentId}/skills");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ProficienciesAsync(list)).Should().Contain(skill, 7);

        using var remove = await acme.DeleteAsync($"{AgentsPath}/{agentId}/skills/{skill}");
        remove.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AgentSkillsAsync(acme, agentId)).Should().BeEmpty();
    }

    // ─── Agent skill assignments are audited ─────────────────────────────────────

    /// <summary>
    /// The replaced assignment and the new one, as the write saw them. The second row changes the name's case
    /// (spec-audit F1): the replaced assignment keeps the spelling it had, the new one carries the new spelling.
    /// </summary>
    public static TheoryData<string, string> AssignmentSpellings => new()
    {
        { "billing", "billing" },
        { "Billing", "billing" },
    };

    [Theory]
    [MemberData(nameof(AssignmentSpellings))]
    public async Task AssignSkill_ShouldRecordTheReplacedAndTheNewAssignment_WhenTheAgentHoldsTheSkill(
        string firstSpelling, string secondSpelling)
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var suffix = Guid.NewGuid().ToString("N");
        var first = $"{firstSpelling}-{suffix}";
        var second = $"{secondSpelling}-{suffix}";
        var agentId = await SeedAgentAsync(Acme);
        await AssignAsync(acme, agentId, first, 5);

        using var response = await acme.PostAsync($"{AgentsPath}/{agentId}/skills",
            JsonContent.Create(new { skillName = second, proficiency = 9 }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var entries = await AuditEntriesAsync(Acme, "skill.assigned", agentId);
        entries.Should().HaveCount(2, "each of the two assigns to agent {0} records one entry", agentId);
        entries.Should().OnlyContain(e => e.TargetType == "agent" && e.Category == "config");

        var firstEntry = entries.Single(e => AuditedAssignment(e.Changes!.After)["proficiency"]!.GetValue<int>() == 5);
        firstEntry.Changes!.Before.Should().BeNull("the first assign replaced nothing");
        AuditedAssignment(firstEntry.Changes.After)["skillName"]!.GetValue<string>().Should().Be(first);

        var secondEntry = entries.Single(e => AuditedAssignment(e.Changes!.After)["proficiency"]!.GetValue<int>() == 9);
        var before = AuditedAssignment(secondEntry.Changes!.Before);
        var after = AuditedAssignment(secondEntry.Changes.After);
        before["agentId"]!.GetValue<string>().Should().Be(agentId);
        before["skillName"]!.GetValue<string>().Should().Be(first, "the replaced assignment keeps its own spelling");
        before["proficiency"]!.GetValue<int>().Should().Be(5);
        after["agentId"]!.GetValue<string>().Should().Be(agentId);
        after["skillName"]!.GetValue<string>().Should().Be(second);
        after["proficiency"]!.GetValue<int>().Should().Be(9);
    }

    [Fact]
    public async Task RemoveAgentSkill_ShouldRecordOneEntry_WhenOnlyTheFirstRemovalRemovesAnAssignment()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var billing = UniqueSkill("billing");
        var spanish = UniqueSkill("spanish");
        var agentId = await SeedAgentAsync(Acme);
        await AssignAsync(acme, agentId, billing, 6);

        using var removeHeld = await acme.DeleteAsync($"{AgentsPath}/{agentId}/skills/{billing}");
        using var removeNotHeld = await acme.DeleteAsync($"{AgentsPath}/{agentId}/skills/{spanish}");

        removeHeld.StatusCode.Should().Be(HttpStatusCode.NoContent);
        removeNotHeld.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var entries = await AuditEntriesAsync(Acme, "skill.unassigned", agentId);
        entries.Should().ContainSingle("only the removal of {0} removed an assignment", billing);
        var entry = entries[0];
        entry.TargetType.Should().Be("agent");
        var removed = AuditedAssignment(entry.Changes!.Before);
        removed["agentId"]!.GetValue<string>().Should().Be(agentId);
        removed["skillName"]!.GetValue<string>().Should().Be(billing);
        removed["proficiency"]!.GetValue<int>().Should().Be(6);
        entry.Changes.After.Should().BeNull();
    }

    [Fact]
    public async Task AssignSkill_ShouldRecordNoEntry_WhenTheAgentBelongsToAnotherTenant()
    {
        using var globex = _factory.CreateAdminClient(Globex);
        var agentId = await SeedAgentAsync(Acme);

        using var response = await globex.PostAsync($"{AgentsPath}/{agentId}/skills",
            JsonContent.Create(new { skillName = UniqueSkill("billing"), proficiency = 3 }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AuditEntriesAsync(Acme, "skill.assigned", agentId)).Should().BeEmpty();
        (await AuditEntriesAsync(Globex, "skill.assigned", agentId)).Should().BeEmpty();
    }

    // ─── Membership penalties read the tenant's own proficiencies ────────────────

    [Fact]
    public async Task ComputeEffectiveMembers_ShouldKeepTheDefaultPenalty_WhenAnotherTenantAssignsTheSkill()
    {
        using var globex = _factory.CreateAdminClient(Globex);
        var skill = UniqueSkill("billing");
        var (queueId, agentId) = await SeedSkillQueueAsync(skill);

        using var response = await globex.PostAsync($"{AgentsPath}/{agentId}/skills",
            JsonContent.Create(new { skillName = skill, proficiency = 10 }));

        var penalty = await PenaltyAsync(queueId, agentId);
        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            penalty.Should().Be(5,
                "no proficiency of acme's agent {0} is recorded, so the default 5 applies", agentId);
        }
    }

    [Fact]
    public async Task ComputeEffectiveMembers_ShouldUseTheTenantsProficiency_WhenTheTenantAssignsTheSkill()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var skill = UniqueSkill("billing");
        var (queueId, agentId) = await SeedSkillQueueAsync(skill);

        await AssignAsync(acme, agentId, skill, 9);

        (await PenaltyAsync(queueId, agentId)).Should().Be(1);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static string UniqueSkill(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<string> SeedAgentAsync(
        string tenantId, IReadOnlyList<string>? skills = null, string? extension = null, string? sipPassword = null)
    {
        var agentId = $"a-{Guid.NewGuid():N}";
        await _factory.Services.GetRequiredService<IAgentStore>().SaveAsync(new Agent
        {
            AgentId = EntityId.From(agentId),
            TenantId = new TenantId(tenantId),
            UserId = EntityId.From($"user-{agentId}"),
            DisplayName = $"Agent {agentId}",
            State = AgentState.Available,
            Skills = skills ?? [],
            Extension = extension,
            SipPassword = sipPassword,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        return agentId;
    }

    /// <summary>An active <c>acme</c> queue requiring <paramref name="skill"/> and a provisioned agent holding it.</summary>
    private async Task<(string QueueId, string AgentId)> SeedSkillQueueAsync(string skill)
    {
        var queueId = $"q-{Guid.NewGuid():N}";
        await _factory.Services.GetRequiredService<IQueueStore>().SaveAsync(new Queue
        {
            QueueId = EntityId.From(queueId),
            TenantId = new TenantId(Acme),
            Name = queueId,
            IsActive = true,
            RequiredSkills = [skill],
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        var extension = Random.Shared.Next(100_000, 999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var agentId = await SeedAgentAsync(Acme, [skill], extension, "secret");
        return (queueId, agentId);
    }

    private async Task<int> PenaltyAsync(string queueId, string agentId)
    {
        var members = await _factory.Services.GetRequiredService<QueueMembershipService>()
            .ComputeEffectiveMembersAsync(Acme, CancellationToken.None);
        return members.Single(m => m.QueueId == queueId && m.AgentId == agentId).Penalty;
    }

    private static async Task CreateSkillAsync(HttpClient client, string name, string category)
    {
        using var response = await client.PostAsync(SkillsPath,
            JsonContent.Create(new { name, category, description = default(string) }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task AssignAsync(HttpClient client, string agentId, string skillName, int proficiency)
    {
        using var response = await client.PostAsync($"{AgentsPath}/{agentId}/skills",
            JsonContent.Create(new { skillName, proficiency }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task<List<string>> SkillNamesAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
            .Select(n => n!["name"]!.GetValue<string>()).ToList();

    private static async Task<string?> CategoryOfAsync(HttpClient client, string name)
    {
        using var response = await client.GetAsync(SkillsPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var skill = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
            .Single(n => string.Equals(n!["name"]!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
        return skill!["category"]?.GetValue<string>();
    }

    /// <summary>The agent's skills read through its own tenant's route, as skill name → proficiency.</summary>
    private static async Task<Dictionary<string, int>> AgentSkillsAsync(HttpClient client, string agentId)
    {
        using var response = await client.GetAsync($"{AgentsPath}/{agentId}/skills");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ProficienciesAsync(response);
    }

    private static async Task<Dictionary<string, int>> ProficienciesAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
            .ToDictionary(n => n!["skillName"]!.GetValue<string>(), n => n!["proficiency"]!.GetValue<int>());

    private async Task<IReadOnlyList<AuditEntry>> AuditEntriesAsync(string tenantId, string action, string targetId)
    {
        using var scope = _factory.Services.CreateScope();
        var page = await scope.ServiceProvider.GetRequiredService<IAuditStore>().SearchAsync(
            new TenantId(tenantId), new AuditQuery(Action: action, TargetId: targetId, Page: 1, PageSize: 200),
            CancellationToken.None);
        return page.Items;
    }

    /// <summary>
    /// An audited assignment: a JSON string (serialized through the source-generated context, so it survives the
    /// Native AOT image) that parses to an object with the <c>AgentSkillDto</c> fields.
    /// </summary>
    private static JsonObject AuditedAssignment(object? value)
    {
        value.Should().BeOfType<string>("an audited assignment is stored as a JSON string, not as a record");
        var node = JsonNode.Parse((string)value!);
        node.Should().BeOfType<JsonObject>();
        var assignment = (JsonObject)node!;
        assignment.Select(kv => kv.Key).Should().Contain(["agentId", "skillName", "proficiency"]);
        return assignment;
    }
}
