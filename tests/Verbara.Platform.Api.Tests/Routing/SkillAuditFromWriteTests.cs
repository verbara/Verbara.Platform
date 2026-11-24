using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests.Routing;

/// <summary>
/// routing-skills-stay-in-their-tenant, design D5: the audited assignment of <c>skill.assigned</c> and
/// <c>skill.unassigned</c> is the one the write itself replaced or deleted, never one read before it. The store double
/// answers its reads with a value other than the one its writes return, as a concurrent write from another replica
/// would leave it, and counts those reads: a handler that reads before writing records the read value and is seen
/// reading. No concurrency is involved, so no widened window is owed.
/// </summary>
public sealed class SkillAuditFromWriteTests
{
    private const string AgentsPath = "/api/admin/agents";

    /// <summary>
    /// The spelling of the assignment the write replaced. The second row changes the name's case (spec-audit F1): the
    /// replaced assignment keeps its own spelling, the new one carries the request's.
    /// </summary>
    public static TheoryData<string> ReplacedSpellings => new() { "billing", "Billing" };

    [Theory]
    [MemberData(nameof(ReplacedSpellings))]
    public async Task AssignSkill_ShouldRecordTheAssignmentTheWriteReplaced_WhenAReadReportsAnotherProficiency(
        string replacedSpelling)
    {
        const string agentId = "a-1";
        var agent = EntityId.From(agentId);
        var catalog = new WriteReportingSkillCatalog(
            replaced: new AgentSkillProficiency(agent, replacedSpelling, 5),
            deleted: null,
            read: new AgentSkillProficiency(agent, "billing", 7));
        using var host = new SkillCatalogDoubleFactory(catalog);
        await SeedAgentAsync(host, agentId);
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.PostAsync($"{AgentsPath}/{agentId}/skills",
            JsonContent.Create(new { skillName = "billing", proficiency = 9 }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var entry = (await AuditEntriesAsync(host, "skill.assigned", agentId)).Should().ContainSingle().Subject;
        var before = AuditedAssignment(entry.Changes!.Before);
        var after = AuditedAssignment(entry.Changes.After);
        using (new AssertionScope())
        {
            before["agentId"]!.GetValue<string>().Should().Be(agentId);
            before["skillName"]!.GetValue<string>().Should().Be(replacedSpelling,
                "the replaced assignment is the one the write returned, with its own spelling");
            before["proficiency"]!.GetValue<int>().Should().Be(5,
                "the write replaced 5; 7 is what a read of the catalog reports");
            after["agentId"]!.GetValue<string>().Should().Be(agentId);
            after["skillName"]!.GetValue<string>().Should().Be("billing");
            after["proficiency"]!.GetValue<int>().Should().Be(9);
            catalog.Reads.Should().Be(0, "the handler reads no assignment before it writes");
        }
    }

    [Fact]
    public async Task RemoveAgentSkill_ShouldRecordTheAssignmentTheWriteDeleted_WhenAReadReportsAnotherProficiency()
    {
        const string agentId = "a-1";
        var agent = EntityId.From(agentId);
        var catalog = new WriteReportingSkillCatalog(
            replaced: null,
            deleted: new AgentSkillProficiency(agent, "billing", 9),
            read: new AgentSkillProficiency(agent, "billing", 5));
        using var host = new SkillCatalogDoubleFactory(catalog);
        await SeedAgentAsync(host, agentId);
        using var client = host.CreateAuthenticatedClient();

        using var response = await client.DeleteAsync($"{AgentsPath}/{agentId}/skills/billing");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var entry = (await AuditEntriesAsync(host, "skill.unassigned", agentId)).Should().ContainSingle().Subject;
        var before = AuditedAssignment(entry.Changes!.Before);
        using (new AssertionScope())
        {
            before["agentId"]!.GetValue<string>().Should().Be(agentId);
            before["skillName"]!.GetValue<string>().Should().Be("billing");
            before["proficiency"]!.GetValue<int>().Should().Be(9,
                "the write deleted 9; 5 is what a read of the catalog reports");
            entry.Changes.After.Should().BeNull();
            catalog.Reads.Should().Be(0, "the handler reads no assignment before it deletes");
        }
    }

    [Fact]
    public void AuditedAssignment_ShouldWriteTheCamelCaseJsonObject_WhenGivenAnAssignment()
    {
        // The Storage.Postgres audit test records this exact text as a literal (that project does not reference the
        // Api), so the two stay tied here.
        SkillEndpoints.AuditedAssignment(new AgentSkillDto("a-1", "Billing", 5))
            .Should().Be("""{"agentId":"a-1","skillName":"Billing","proficiency":5}""");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task SeedAgentAsync(UnifiedPlatformApiFactory host, string agentId) =>
        await host.AgentStore.SaveAsync(new Agent
        {
            AgentId = EntityId.From(agentId),
            TenantId = new TenantId(UnifiedPlatformApiFactory.TestTenantId),
            UserId = EntityId.From($"user-{agentId}"),
            DisplayName = $"Agent {agentId}",
            State = AgentState.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

    private static async Task<IReadOnlyList<AuditEntry>> AuditEntriesAsync(
        UnifiedPlatformApiFactory host, string action, string targetId)
    {
        using var scope = host.Services.CreateScope();
        var page = await scope.ServiceProvider.GetRequiredService<IAuditStore>().SearchAsync(
            new TenantId(UnifiedPlatformApiFactory.TestTenantId),
            new AuditQuery(Action: action, TargetId: targetId, Page: 1, PageSize: 50),
            CancellationToken.None);
        return page.Items;
    }

    private static JsonObject AuditedAssignment(object? value)
    {
        value.Should().BeOfType<string>("an audited assignment is stored as a JSON string, not as a record");
        var node = JsonNode.Parse((string)value!);
        node.Should().BeOfType<JsonObject>();
        return (JsonObject)node!;
    }

    /// <summary>A host of its own whose skill catalog is the double.</summary>
    private sealed class SkillCatalogDoubleFactory(ISkillCatalogStore catalog) : UnifiedPlatformApiFactory
    {
        protected override ISkillCatalogStore CreateSkillCatalogStore() => catalog;
    }

    /// <summary>
    /// A catalog whose writes return <c>replaced</c> and <c>deleted</c> while every read of an assignment reports
    /// <c>read</c>, counting the reads of one agent's skills and of one skill's agents.
    /// </summary>
    private sealed class WriteReportingSkillCatalog(
        AgentSkillProficiency? replaced, AgentSkillProficiency? deleted, AgentSkillProficiency read) : ISkillCatalogStore
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Task<IReadOnlyList<SkillDefinition>> ListSkillsAsync(TenantId tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SkillDefinition>>([new SkillDefinition(read.SkillName, null, null)]);

        public Task<SkillDefinition> UpsertSkillAsync(TenantId tenantId, SkillDefinition skill, CancellationToken ct) =>
            Task.FromResult(skill);

        public Task<SkillRemoval> RemoveSkillAsync(TenantId tenantId, string name, bool force, CancellationToken ct) =>
            Task.FromResult(SkillRemoval.NotFound);

        public Task<IReadOnlyList<AgentSkillProficiency>> ListAgentsWithSkillAsync(
            TenantId tenantId, string skillName, CancellationToken ct) => ReadAsync();

        public Task<IReadOnlyList<AgentSkillProficiency>> ListAgentSkillsAsync(
            TenantId tenantId, EntityId agentId, CancellationToken ct) => ReadAsync();

        // Not counted: the queue membership computation reads every proficiency in the background, whatever the routes do.
        public Task<IReadOnlyList<AgentSkillProficiency>> ListProficienciesAsync(TenantId tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentSkillProficiency>>([read]);

        public Task<SkillAssignment> AssignAsync(TenantId tenantId, AgentSkillProficiency assignment, CancellationToken ct) =>
            Task.FromResult(SkillAssignment.Assigned(assignment, replaced));

        public Task<AgentSkillProficiency?> UnassignAsync(
            TenantId tenantId, EntityId agentId, string skillName, CancellationToken ct) =>
            Task.FromResult(deleted);

        private Task<IReadOnlyList<AgentSkillProficiency>> ReadAsync()
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult<IReadOnlyList<AgentSkillProficiency>>([read]);
        }
    }
}
