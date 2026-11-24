using FluentAssertions.Execution;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres.Stores;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// routing-skills-stay-in-their-tenant task 2.1 (design D1, D2, D4, D5, D9): <see cref="PostgresSkillCatalogStore"/> on
/// the real embedded migrations, on <see cref="PinnedPostgres18MigrationsFixture"/> (the image and digest its
/// statements were measured on, card PLT-SKL-01). Each test uses tenants of its own.
/// </summary>
/// <remarks>
/// The interleavings mirror the measured arms. Each is sequenced by observing the waiting backend in
/// <c>pg_stat_activity</c> (<c>wait_event_type = 'Lock'</c>), never by sleeps: this class owns its container and xUnit
/// runs its tests one at a time, so any backend waiting on a lock is the write under test. A write the test holds
/// open is always committed before any assertion runs, so a failed assertion never leaves a lock behind. Each
/// negative control runs the same scenario and the same assertions against a test-local variant that lacks the
/// property, and asserts that those assertions fail; the failure message is written to the test output.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostgresSkillCatalogStoreTests : IClassFixture<PinnedPostgres18MigrationsFixture>
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly PinnedPostgres18MigrationsFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly PostgresSkillCatalogStore _store;

    public PostgresSkillCatalogStoreTests(PinnedPostgres18MigrationsFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _store = new PostgresSkillCatalogStore(fixture.DataSource);
    }

    // ── Substrate ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fixture_ShouldRunPostgres18_WhenThePinnedImageStarts()
    {
        PinnedPostgres18MigrationsFixture.Image.Should().Be(
            "postgres:18-alpine@sha256:0115267ab5a509022df9319c77be365cef335adb71556c75f60f5360ee247e71");
        await using var cmd = _fixture.DataSource.CreateCommand("SELECT version()");
        var version = (string)(await cmd.ExecuteScalarAsync())!;
        _output.WriteLine(version);
        version.Should().StartWith("PostgreSQL 18.7 ");
    }

    // ── Skill definitions ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertSkillAsync_ShouldLeaveTheOtherTenantsSkillUnchanged_WhenBothTenantsUseTheSameName()
    {
        var (t1, t2) = (NewTenant(), NewTenant());
        await _store.UpsertSkillAsync(t1, new SkillDefinition("billing", "product", "acme's"), None);

        await _store.UpsertSkillAsync(t2, new SkillDefinition("billing", "finance", null), None);

        (await _store.ListSkillsAsync(t1, None)).Should().Equal(new SkillDefinition("billing", "product", "acme's"));
        (await _store.ListSkillsAsync(t2, None)).Should().Equal(new SkillDefinition("billing", "finance", null));
    }

    [Fact]
    public async Task UpsertSkillAsync_ShouldKeepOneRowWithTheNewSpelling_WhenTheNameDiffersOnlyInCase()
    {
        var tenant = NewTenant();
        await _store.UpsertSkillAsync(tenant, new SkillDefinition("Billing", "product", "first"), None);

        var stored = await _store.UpsertSkillAsync(tenant, new SkillDefinition("billing", "finance", null), None);

        stored.Should().Be(new SkillDefinition("billing", "finance", null), "the upsert returns the row it stored");
        (await CountAsync("SELECT count(*) FROM routing_skills WHERE tenant_id = @TenantId", tenant))
            .Should().Be(1, "Billing and billing are one skill of the tenant");
        (await _store.ListSkillsAsync(tenant, None)).Should().Equal(new SkillDefinition("billing", "finance", null));
    }

    // ── Tenant isolation of the agent members ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentMembers_ShouldReadAndWriteOnlyTheTenantsRows_WhenTwoTenantsHaveAnAgentOfTheSameId()
    {
        var (t1, t2) = (NewTenant(), NewTenant());
        var agent = EntityId.From("a-1");
        await NewAgentAsync(t1, agent);
        await NewAgentAsync(t2, agent);
        await AssignAsync(t1, agent, "billing", 8);
        await AssignAsync(t2, agent, "billing", 2);
        await AssignAsync(t2, agent, "spanish", 4);

        (await _store.ListAgentSkillsAsync(t1, agent, None)).Should().Equal(new AgentSkillProficiency(agent, "billing", 8));
        (await _store.ListAgentsWithSkillAsync(t1, "BILLING", None)).Should().Equal(new AgentSkillProficiency(agent, "billing", 8));
        (await _store.ListProficienciesAsync(t1, None)).Should().Equal(new AgentSkillProficiency(agent, "billing", 8));
        (await _store.ListProficienciesAsync(t2, None)).Should().Equal(
            new AgentSkillProficiency(agent, "billing", 2), new AgentSkillProficiency(agent, "spanish", 4));

        (await _store.UnassignAsync(t2, agent, "billing", None)).Should().Be(new AgentSkillProficiency(agent, "billing", 2));

        (await _store.ListAgentSkillsAsync(t1, agent, None))
            .Should().Equal([new AgentSkillProficiency(agent, "billing", 8)], "t2's unassign removes only t2's row");
        (await _store.ListAgentSkillsAsync(t2, agent, None)).Should().Equal(new AgentSkillProficiency(agent, "spanish", 4));
    }

    [Fact]
    public async Task AssignAsync_ShouldReportNoSuchAgentAndStoreNothing_WhenTheTenantHasNoSuchAgent()
    {
        var (t1, t2) = (NewTenant(), NewTenant());
        var agent = EntityId.From("a-1");
        await NewAgentAsync(t1, agent);

        var otherTenants = await _store.AssignAsync(t2, new AgentSkillProficiency(agent, "billing", 9), None);
        var ghost = await _store.AssignAsync(t1, new AgentSkillProficiency(EntityId.From("ghost"), "billing", 9), None);

        otherTenants.Should().Be(SkillAssignment.NoSuchAgent, "a-1 is t1's agent, not t2's");
        ghost.Should().Be(SkillAssignment.NoSuchAgent, "no tenant has an agent ghost");
        (await CountAsync("SELECT count(*) FROM agent_skill_proficiencies WHERE tenant_id = ANY(@Tenants)", t1, t2))
            .Should().Be(0);
    }

    [Fact]
    public async Task AssignAsync_ShouldReportNoSuchAgent_WhenTheAgentDeleteCommitsWhileTheAssignWaits()
    {
        // Measured arm (c): the assign that races an uncommitted agent delete waits on the agent row, then fails the
        // foreign key (23503), which the store reports as no such agent.
        var tenant = NewTenant();
        var agent = EntityId.From("a-1");
        await NewAgentAsync(tenant, agent);
        var delete = await HoldSqlAsync(
            ("SELECT user_id FROM agents WHERE tenant_id = @TenantId AND agent_id = @AgentId FOR UPDATE", Key(tenant, agent)),
            (PostgresAgentStore.DeleteSql, Key(tenant, agent)));

        var run = await InterleaveAsync(delete, () => _store.AssignAsync(tenant, new AgentSkillProficiency(agent, "billing", 9), None));

        run.Result.Should().Be(SkillAssignment.NoSuchAgent);
        run.WaitEvent.Should().Be("transactionid", "the assign waits on the agent row the delete holds");
        (await CountAsync("SELECT count(*) FROM agent_skill_proficiencies WHERE tenant_id = @TenantId", tenant)).Should().Be(0);
    }

    // ── The cascade on agent delete ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentDelete_ShouldRemoveOnlyThatAgentsAssignments_WhenTheAgentIsDeleted()
    {
        var (t1, t2) = (NewTenant(), NewTenant());
        var (a1, a2) = (EntityId.From("a-1"), EntityId.From("a-2"));
        foreach (var (tenant, agent) in new[] { (t1, a1), (t1, a2), (t2, a1) })
        {
            await NewAgentAsync(tenant, agent);
            await AssignAsync(tenant, agent, "billing", 5);
            await AssignAsync(tenant, agent, "spanish", 6);
        }

        await new PostgresAgentStore(_fixture.DataSource).DeleteAsync(t1, a1, None);

        (await _store.ListAgentSkillsAsync(t1, a1, None)).Should().BeEmpty("the agent's rows go with the agent");
        (await _store.ListAgentSkillsAsync(t1, a2, None)).Should().HaveCount(2, "another agent of the tenant keeps its rows");
        (await _store.ListAgentSkillsAsync(t2, a1, None)).Should().HaveCount(2, "another tenant's agent of the same id keeps its rows");
        (await _store.ListAgentsWithSkillAsync(t1, "billing", None)).Select(p => p.AgentId).Should().Equal(a2);
    }

    // ── The replica property ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoStoreInstances_ShouldReadTheSameRows_WhenTheyShareOneDatabase()
    {
        var tenant = NewTenant();
        var agent = EntityId.From("a-1");
        await NewAgentAsync(tenant, agent);
        var first = new PostgresSkillCatalogStore(_fixture.DataSource);
        await using var otherSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var second = new PostgresSkillCatalogStore(otherSource);

        await first.UpsertSkillAsync(tenant, new SkillDefinition("billing", "product", null), None);
        await first.AssignAsync(tenant, new AgentSkillProficiency(agent, "billing", 8), None);

        (await second.ListSkillsAsync(tenant, None)).Should().Equal(new SkillDefinition("billing", "product", null));
        (await second.ListAgentSkillsAsync(tenant, agent, None)).Should().Equal(new AgentSkillProficiency(agent, "billing", 8));
    }

    // ── RemoveSkillAsync's three outcomes (design D4) ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveSkillAsync_ShouldReturnNotFoundAndLeaveTheAssignment_WhenTheTenantDefinesNoSkillOfThatName(bool force)
    {
        var (t1, t2) = (NewTenant(), NewTenant());
        var agent = EntityId.From("a-1");
        await NewAgentAsync(t1, agent);
        await AssignAsync(t1, agent, "spanish", 7);
        await _store.UpsertSkillAsync(t2, new SkillDefinition("spanish", null, null), None);

        var removal = await _store.RemoveSkillAsync(t1, "spanish", force, None);

        removal.Should().Be(SkillRemoval.NotFound, "t1 defines no spanish, whoever holds the name or defines it elsewhere");
        (await _store.ListAgentSkillsAsync(t1, agent, None)).Should().Equal(new AgentSkillProficiency(agent, "spanish", 7));
        (await _store.ListSkillsAsync(t2, None)).Should().ContainSingle("t2's skill of that name is not touched");
    }

    [Fact]
    public async Task RemoveSkillAsync_ShouldReturnInUseWithTheTenantsHoldersAndRemoveNothing_WhenNotForced()
    {
        var (t1, t2) = (NewTenant(), NewTenant());
        var (a1, a2, other) = (EntityId.From("a-1"), EntityId.From("a-2"), EntityId.From("a-3"));
        await _store.UpsertSkillAsync(t1, new SkillDefinition("billing", null, null), None);
        foreach (var (tenant, agent) in new[] { (t1, a1), (t1, a2), (t2, a1), (t2, other) })
        {
            await NewAgentAsync(tenant, agent);
            await AssignAsync(tenant, agent, "billing", 5);
        }

        var removal = await _store.RemoveSkillAsync(t1, "Billing", force: false, None);

        removal.Should().Be(SkillRemoval.InUse(2), "only t1's two holders are counted");
        (await _store.ListSkillsAsync(t1, None)).Should().ContainSingle();
        (await _store.ListAgentsWithSkillAsync(t1, "billing", None)).Should().HaveCount(2);
        (await _store.ListAgentsWithSkillAsync(t2, "billing", None)).Should().HaveCount(2);
    }

    [Fact]
    public async Task RemoveSkillAsync_ShouldRemoveTheDefinitionAndOnlyTheTenantsAssignments_WhenForced()
    {
        var (t1, t2) = (NewTenant(), NewTenant());
        var agent = EntityId.From("a-1");
        await _store.UpsertSkillAsync(t1, new SkillDefinition("billing", null, null), None);
        await _store.UpsertSkillAsync(t2, new SkillDefinition("billing", null, null), None);
        foreach (var tenant in new[] { t1, t2 })
        {
            await NewAgentAsync(tenant, agent);
            await AssignAsync(tenant, agent, "billing", 5);
            await AssignAsync(tenant, agent, "spanish", 6);
        }

        var removal = await _store.RemoveSkillAsync(t1, "BILLING", force: true, None);

        removal.Should().Be(SkillRemoval.Removed);
        (await _store.ListSkillsAsync(t1, None)).Should().BeEmpty();
        (await _store.ListAgentSkillsAsync(t1, agent, None)).Should().Equal(new AgentSkillProficiency(agent, "spanish", 6));
        (await _store.ListSkillsAsync(t2, None)).Should().ContainSingle("t2's billing is another skill");
        (await _store.ListAgentSkillsAsync(t2, agent, None)).Should().HaveCount(2);
    }

    [Fact]
    public async Task RemoveSkillAsync_ShouldRemoveTheDefinition_WhenNoAgentHoldsIt()
    {
        var tenant = NewTenant();
        await _store.UpsertSkillAsync(tenant, new SkillDefinition("billing", null, null), None);

        (await _store.RemoveSkillAsync(tenant, "billing", force: false, None)).Should().Be(SkillRemoval.Removed);
        (await _store.ListSkillsAsync(tenant, None)).Should().BeEmpty();
    }

    // ── The values that feed the audit (sequential) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AssignAsync_ShouldReturnNothingReplaced_WhenItIsTheFirstAssign()
    {
        var tenant = NewTenant();
        var agent = EntityId.From("a-1");
        await NewAgentAsync(tenant, agent);

        var result = await _store.AssignAsync(tenant, new AgentSkillProficiency(agent, "billing", 5), None);

        result.Should().Be(SkillAssignment.Assigned(new AgentSkillProficiency(agent, "billing", 5), null));
    }

    [Theory]
    [InlineData("billing", "billing")]
    [InlineData("Billing", "billing")] // spec-audit F1: the previous spelling comes back from the same write
    public async Task AssignAsync_ShouldReturnTheReplacedAndTheStoredAssignment_WhenTheAgentHoldsTheSkill(
        string firstSpelling, string secondSpelling)
    {
        var (tenant, agent) = await AgentHoldingAsync(firstSpelling, 5);

        var result = await _store.AssignAsync(tenant, new AgentSkillProficiency(agent, secondSpelling, 9), None);

        AssertReplacedFiveStoredNine(result, agent, firstSpelling, secondSpelling);
        (await _store.ListAgentSkillsAsync(tenant, agent, None)).Should().Equal(new AgentSkillProficiency(agent, secondSpelling, 9));
    }

    [Fact]
    public async Task AssignAsync_NegativeControl_ShouldFailTheReplacedAssertion_WhenTheStatementReturnsOnlyTheNewRow()
    {
        var (tenant, agent) = await AgentHoldingAsync("billing", 5);

        // Test-local variant: the same key-locked upsert with a plain RETURNING, which after DO UPDATE is the new row.
        var result = await AssignReturningOnlyAsync(tenant, new AgentSkillProficiency(agent, "billing", 9));

        await ShouldFailAsync(() => AssertReplacedFiveStoredNine(result, agent, "billing", "billing"), "returning-only");
    }

    [Fact]
    public async Task UnassignAsync_ShouldReturnTheDeletedRow_WhenTheAgentHoldsTheSkill_AndNullWhenItDoesNot()
    {
        var (tenant, agent) = await AgentHoldingAsync("Billing", 5);

        (await _store.UnassignAsync(tenant, agent, "billing", None)).Should().Be(new AgentSkillProficiency(agent, "Billing", 5));
        (await _store.UnassignAsync(tenant, agent, "billing", None)).Should().BeNull("nothing is left to delete");
        (await _store.UnassignAsync(tenant, agent, "spanish", None)).Should().BeNull("the agent never held spanish");
        (await _store.ListAgentSkillsAsync(tenant, agent, None)).Should().BeEmpty();
    }

    // ── Interleavings, each mirroring its measured arm ─────────────────────────────────────────────────────────

    // (i) Arm (e): an unassign that starts while an assign from 5 to 9 is uncommitted returns 9, the value it deleted.
    [Fact]
    public async Task UnassignAsync_ShouldReturnTheValueItDeleted_WhenAnAssignCommitsWhileItWaits()
    {
        var (tenant, agent) = await AgentHoldingAsync("billing", 5);

        var run = await UnassignBehindAHeldAssignOfNineAsync(tenant, agent,
            async () => (await _store.UnassignAsync(tenant, agent, "billing", None))?.Proficiency);

        AssertRemovedNine(run);
        (await _store.ListAgentSkillsAsync(tenant, agent, None)).Should().BeEmpty();
    }

    [Fact]
    public async Task UnassignAsync_NegativeControl_ShouldFailTheRemovedAssertion_WhenTheRemovalReadsBeforeItDeletes()
    {
        var (tenant, agent) = await AgentHoldingAsync("billing", 5);

        // Test-local variant: the read-then-delete shape (design D5's defect) reports what it read, not what it deleted.
        var run = await UnassignBehindAHeldAssignOfNineAsync(tenant, agent, async () =>
        {
            var read = (await _store.ListAgentSkillsAsync(tenant, agent, None)).Single().Proficiency;
            await _store.UnassignAsync(tenant, agent, "billing", None);
            return (int?)read;
        });

        await ShouldFailAsync(() => AssertRemovedNine(run), "read-then-delete");
    }

    // (ii) Arm (d): an assign that starts while another assign of 7 is uncommitted reports 7 replaced, with a prior row
    // at 5 and without one (the insert race).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AssignAsync_ShouldReturnTheCommittedPriorValue_WhenAnotherAssignCommitsWhileItWaits(bool rowBefore)
    {
        var tenant = NewTenant();
        var agent = EntityId.From("a-1");
        await NewAgentAsync(tenant, agent);
        if (rowBefore)
            await AssignAsync(tenant, agent, "billing", 5);

        SkillAssignment? first = null;
        var held = await HoldStoreWriteAsync(async s =>
            first = await s.AssignAsync(tenant, new AgentSkillProficiency(agent, "billing", 7), None));

        var run = await InterleaveAsync(held, () => _store.AssignAsync(tenant, new AgentSkillProficiency(agent, "billing", 9), None));

        run.WaitEvent.Should().Be("advisory", "the second assign waits on the key lock the first holds");
        run.CompletedBeforeCommit.Should().BeFalse();
        first.Should().Be(SkillAssignment.Assigned(
            new AgentSkillProficiency(agent, "billing", 7), rowBefore ? new AgentSkillProficiency(agent, "billing", 5) : null));
        run.Result.Should().Be(SkillAssignment.Assigned(
            new AgentSkillProficiency(agent, "billing", 9), new AgentSkillProficiency(agent, "billing", 7)));
        (await _store.ListAgentSkillsAsync(tenant, agent, None)).Should().Equal(new AgentSkillProficiency(agent, "billing", 9));
    }

    // (iii) Arm (f), round-2 audit F1: an assign that starts while an unassign of the row at 5 is uncommitted waits on
    // the key lock, then reports nothing replaced.
    [Fact]
    public async Task AssignAsync_ShouldReturnNothingReplaced_WhenAnUnassignCommitsWhileItWaits()
    {
        var (tenant, agent) = await AgentHoldingAsync("billing", 5);
        AgentSkillProficiency? removed = null;
        var held = await HoldStoreWriteAsync(async s => removed = await s.UnassignAsync(tenant, agent, "billing", None));

        var run = await InterleaveAsync(held, () => _store.AssignAsync(tenant, new AgentSkillProficiency(agent, "billing", 9), None));

        removed.Should().Be(new AgentSkillProficiency(agent, "billing", 5));
        await AssertAssignedNineOverNothingAsync(run, tenant, agent);
    }

    [Fact]
    public async Task AssignAsync_NegativeControl_ShouldFailTheNothingReplacedAssertion_WhenTheUnassignTakesNoKeyLock()
    {
        var (tenant, agent) = await AgentHoldingAsync("billing", 5);

        // Test-local variant: the bare DELETE … RETURNING with no key lock.
        var held = await HoldSqlAsync((PostgresSkillCatalogStore.UnassignSql, Key(tenant, agent, "billing")));

        var run = await InterleaveAsync(held, () => _store.AssignAsync(tenant, new AgentSkillProficiency(agent, "billing", 9), None));

        await ShouldFailAsync(() => AssertAssignedNineOverNothingAsync(run, tenant, agent), "unassign-no-key-lock");
    }

    // (iv) Arm (g): the same, behind a forced skill delete held uncommitted after its deletes.
    [Fact]
    public async Task AssignAsync_ShouldReturnNothingReplaced_WhenAForcedSkillDeleteCommitsWhileItWaits()
    {
        var (tenant, agent) = await AgentHoldingAsync("billing", 5);
        await _store.UpsertSkillAsync(tenant, new SkillDefinition("billing", null, null), None);
        SkillRemoval? removal = null;
        var held = await HoldStoreWriteAsync(async s => removal = await s.RemoveSkillAsync(tenant, "billing", force: true, None));

        var run = await InterleaveAsync(held, () => _store.AssignAsync(tenant, new AgentSkillProficiency(agent, "billing", 9), None));

        removal.Should().Be(SkillRemoval.Removed);
        await AssertAssignedNineOverNothingAsync(run, tenant, agent);
        (await _store.ListSkillsAsync(tenant, None)).Should().BeEmpty("the definition delete committed");
    }

    [Fact]
    public async Task AssignAsync_NegativeControl_ShouldFailTheNothingReplacedAssertion_WhenTheSkillDeleteTakesNoKeyLocks()
    {
        var (tenant, agent) = await AgentHoldingAsync("billing", 5);
        await _store.UpsertSkillAsync(tenant, new SkillDefinition("billing", null, null), None);

        // Test-local variant: the skill delete without step (2) and without `agent_id = ANY(…)`.
        var held = await HoldSqlAsync(
            (PostgresSkillCatalogStore.RemoveSkillLockDefinitionSql, Name(tenant, "billing")),
            ("DELETE FROM agent_skill_proficiencies WHERE tenant_id = @TenantId AND lower(skill_name) = lower(@Name)",
                Name(tenant, "billing")),
            (PostgresSkillCatalogStore.RemoveSkillDeleteDefinitionSql, Name(tenant, "billing")));

        var run = await InterleaveAsync(held, () => _store.AssignAsync(tenant, new AgentSkillProficiency(agent, "billing", 9), None));

        await ShouldFailAsync(() => AssertAssignedNineOverNothingAsync(run, tenant, agent), "remove-skill-no-key-locks");
    }

    // ── Assertions shared by a property test and its negative control ──────────────────────────────────────────

    private static void AssertReplacedFiveStoredNine(
        SkillAssignment result, EntityId agent, string firstSpelling, string secondSpelling)
    {
        using var scope = new AssertionScope();
        result.Outcome.Should().Be(SkillAssignOutcome.Assigned);
        result.Replaced.Should().Be(new AgentSkillProficiency(agent, firstSpelling, 5), "the write replaced the row at 5");
        result.Stored.Should().Be(new AgentSkillProficiency(agent, secondSpelling, 9));
    }

    private static void AssertRemovedNine(Interleaving<int?> run)
    {
        using var scope = new AssertionScope();
        run.WaitEvent.Should().Be("advisory", "the unassign waits on the key lock the assign holds");
        run.CompletedBeforeCommit.Should().BeFalse();
        run.Result.Should().Be(9, "the unassign deleted the row at 9 the assign committed");
    }

    private async Task AssertAssignedNineOverNothingAsync(Interleaving<SkillAssignment> run, TenantId tenant, EntityId agent)
    {
        var rows = await _store.ListAgentSkillsAsync(tenant, agent, None);
        using var scope = new AssertionScope();
        run.WaitEvent.Should().Be("advisory", "the assign waits on the key lock the removal holds");
        run.CompletedBeforeCommit.Should().BeFalse();
        run.Result.Should().Be(
            SkillAssignment.Assigned(new AgentSkillProficiency(agent, "billing", 9), null),
            "the removal deleted the row at 5 before the assign took its snapshot, so the assign replaced nothing");
        rows.Should().Equal(new AgentSkillProficiency(agent, "billing", 9));
    }

    private async Task ShouldFailAsync(Func<Task> assertions, string variant)
    {
        var failure = await assertions.Should().ThrowAsync<XunitException>(
            "the {0} variant lacks the property, so the same assertions must fail against it", variant);
        _output.WriteLine($"[{variant}] {failure.Which.Message}");
    }

    private Task ShouldFailAsync(Action assertions, string variant) =>
        ShouldFailAsync(() => { assertions(); return Task.CompletedTask; }, variant);

    // ── Scenario drivers ───────────────────────────────────────────────────────────────────────────────────────

    private async Task<Interleaving<int?>> UnassignBehindAHeldAssignOfNineAsync(
        TenantId tenant, EntityId agent, Func<Task<int?>> unassign)
    {
        var held = await HoldStoreWriteAsync(s => s.AssignAsync(tenant, new AgentSkillProficiency(agent, "billing", 9), None));
        return await InterleaveAsync(held, unassign);
    }

    /// <summary>
    /// Starts <paramref name="waiter"/> while <paramref name="held"/> holds its write open, waits until a backend is
    /// seen waiting on a lock (or the waiter returns), commits the held write, and only then awaits the waiter.
    /// </summary>
    private async Task<Interleaving<T>> InterleaveAsync<T>(HeldWrite held, Func<Task<T>> waiter)
    {
        Task<T> task;
        string? waitEvent;
        bool completedBeforeCommit;
        try
        {
            task = waiter();
            waitEvent = await WaitForALockWaitAsync(task);
            completedBeforeCommit = task.IsCompleted;
        }
        finally
        {
            await held.CommitAsync();
        }

        return new Interleaving<T>(await task, waitEvent, completedBeforeCommit);
    }

    /// <summary>The <c>wait_event</c> of the first backend seen waiting on a lock, or null if the waiter returned first.</summary>
    private async Task<string?> WaitForALockWaitAsync(Task waiter)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using var probe = _fixture.DataSource.CreateCommand(
                "SELECT wait_event FROM pg_stat_activity " +
                "WHERE wait_event_type = 'Lock' AND datname = current_database() AND pid <> pg_backend_pid() LIMIT 1");
            if (await probe.ExecuteScalarAsync() is string waitEvent)
                return waitEvent;
            if (waiter.IsCompleted)
                return null;
        }

        throw new TimeoutException("The second writer neither waited on a lock nor returned.");
    }

    /// <summary>Runs a store write with every write of that instance held open before its commit.</summary>
    private async Task<HeldWrite> HoldStoreWriteAsync(Func<PostgresSkillCatalogStore, Task> write)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new PostgresSkillCatalogStore(_fixture.DataSource)
        {
            BeforeCommit = async () =>
            {
                reached.TrySetResult();
                await release.Task;
            },
        };

        var running = write(holder);
        if (await Task.WhenAny(reached.Task, running) == running)
            await running; // it returned before its commit point: surface its failure
        return new HeldWrite(async () =>
        {
            release.TrySetResult();
            await running;
        });
    }

    /// <summary>Runs test-local statements in one READ COMMITTED transaction and holds it open.</summary>
    private async Task<HeldWrite> HoldSqlAsync(params (string Sql, Action<NpgsqlParameterCollection> Bind)[] statements)
    {
        var conn = await _fixture.DataSource.OpenConnectionAsync();
        var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        foreach (var (sql, bind) in statements)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            bind(cmd.Parameters);
            await cmd.ExecuteNonQueryAsync();
        }

        return new HeldWrite(async () =>
        {
            await tx.CommitAsync();
            await tx.DisposeAsync();
            await conn.DisposeAsync();
        });
    }

    private async Task<SkillAssignment> AssignReturningOnlyAsync(TenantId tenant, AgentSkillProficiency assignment)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        await using (var keyLock = new NpgsqlCommand(PostgresSkillCatalogStore.KeyLockSql, conn, tx))
        {
            Key(tenant, assignment.AgentId, assignment.SkillName)(keyLock.Parameters);
            await keyLock.ExecuteNonQueryAsync();
        }

        await using var assign = new NpgsqlCommand(
            "INSERT INTO agent_skill_proficiencies (tenant_id, agent_id, skill_name, proficiency, created_at, updated_at) " +
            "VALUES (@TenantId, @AgentId, @SkillName, @Proficiency, now(), now()) " +
            "ON CONFLICT (tenant_id, agent_id, lower(skill_name)) DO UPDATE " +
            "SET skill_name = EXCLUDED.skill_name, proficiency = EXCLUDED.proficiency, updated_at = now() " +
            "RETURNING skill_name, proficiency", conn, tx);
        Key(tenant, assignment.AgentId, assignment.SkillName)(assign.Parameters);
        assign.Parameters.Add(new NpgsqlParameter("Proficiency", NpgsqlDbType.Integer) { Value = assignment.Proficiency });
        AgentSkillProficiency returned;
        await using (var reader = await assign.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            returned = new AgentSkillProficiency(assignment.AgentId, reader.GetString(0), reader.GetInt32(1));
        }

        await tx.CommitAsync();
        // The shape the audit flagged: the returned row taken as the replaced one.
        return SkillAssignment.Assigned(returned, returned);
    }

    // ── Data helpers ───────────────────────────────────────────────────────────────────────────────────────────

    private static TenantId NewTenant() => new($"t-{Guid.NewGuid():N}");

    private async Task<(TenantId Tenant, EntityId Agent)> AgentHoldingAsync(string skillName, int proficiency)
    {
        var tenant = NewTenant();
        var agent = EntityId.From("a-1");
        await NewAgentAsync(tenant, agent);
        await AssignAsync(tenant, agent, skillName, proficiency);
        return (tenant, agent);
    }

    private Task NewAgentAsync(TenantId tenant, EntityId agent) =>
        AgentInsert.InsertAsync(_fixture.DataSource, new Agent
        {
            AgentId = agent,
            TenantId = tenant,
            UserId = EntityId.New(),
            DisplayName = agent.Value,
            State = AgentState.Offline,
            CreatedAt = DateTimeOffset.UtcNow,
        });

    private async Task AssignAsync(TenantId tenant, EntityId agent, string skillName, int proficiency) =>
        (await _store.AssignAsync(tenant, new AgentSkillProficiency(agent, skillName, proficiency), None))
            .Outcome.Should().Be(SkillAssignOutcome.Assigned);

    private async Task<long> CountAsync(string sql, params TenantId[] tenants)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(sql);
        if (tenants.Length == 1)
            cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenants[0].Value });
        else
            cmd.Parameters.Add(new NpgsqlParameter("Tenants", NpgsqlDbType.Array | NpgsqlDbType.Text)
                { Value = tenants.Select(t => t.Value).ToArray() });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static Action<NpgsqlParameterCollection> Key(TenantId tenant, EntityId agent) => p =>
    {
        p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenant.Value });
        p.Add(new NpgsqlParameter("AgentId", NpgsqlDbType.Text) { Value = agent.Value });
    };

    private static Action<NpgsqlParameterCollection> Key(TenantId tenant, EntityId agent, string skillName) => p =>
    {
        Key(tenant, agent)(p);
        p.Add(new NpgsqlParameter("SkillName", NpgsqlDbType.Text) { Value = skillName });
    };

    private static Action<NpgsqlParameterCollection> Name(TenantId tenant, string name) => p =>
    {
        p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenant.Value });
        p.Add(new NpgsqlParameter("Name", NpgsqlDbType.Text) { Value = name });
    };

    private sealed record HeldWrite(Func<Task> CommitAsync);

    private sealed record Interleaving<T>(T Result, string? WaitEvent, bool CompletedBeforeCommit);
}
