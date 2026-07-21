using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Analytics;
using Verbara.Sdk.Pro.CallAnalytics.Domain;
using Verbara.Sdk.Pro.EventStore;

namespace Verbara.Platform.Api.Tests;

public sealed class AnalyticsEndpointTests : IClassFixture<UnifiedPlatformApiFactory>
{
    // All test methods spin up their own isolated UnifiedPlatformApiFactory instances
    // so the class fixture is kept only to satisfy xUnit's IClassFixture contract.
    public AnalyticsEndpointTests(UnifiedPlatformApiFactory factory) { _ = factory; }

    // ─── Dashboard ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDashboard_ShouldReturnKpis_WhenSnapshotsExist()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        await factory.SnapshotStore.UpsertAsync(MakeSnapshot(factory, "support", now.AddHours(-1),
            callsOffered: 50, callsAnswered: 40, slaMetCount: 35, totalWaitMs: 80_000));

        var response = await client.GetAsync("/api/analytics/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["kpis"]!["conversationsHandled"]!.GetValue<int>().Should().Be(40);
    }

    [Fact]
    public async Task GetDashboard_ShouldReturnZeros_WhenNoData()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/analytics/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["kpis"]!["conversationsHandled"]!.GetValue<int>().Should().Be(0);
        json["kpis"]!["slaPercent"]!.GetValue<double>().Should().Be(0.0);
    }

    [Fact]
    public async Task GetDashboard_ShouldComputeWeightedSla_WhenMultipleIntervals()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        // Interval 1: 20 offered, 18 sla met → 90%
        await factory.SnapshotStore.UpsertAsync(MakeSnapshot(factory, "q1", now.AddHours(-3),
            callsOffered: 20, callsAnswered: 20, slaMetCount: 18));
        // Interval 2: 20 offered, 10 sla met → 50%
        await factory.SnapshotStore.UpsertAsync(MakeSnapshot(factory, "q1", now.AddHours(-2),
            callsOffered: 20, callsAnswered: 20, slaMetCount: 10));

        var response = await client.GetAsync("/api/analytics/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        // Combined: 28 sla met / 40 offered = 70%
        var sla = json!["kpis"]!["slaPercent"]!.GetValue<double>();
        sla.Should().BeApproximately(70.0, 0.1);
    }

    [Fact]
    public async Task GetDashboard_ShouldReturnPreviousPeriodKpis_ForDeltas()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        // Only current period has data
        await factory.SnapshotStore.UpsertAsync(MakeSnapshot(factory, "support", now.AddHours(-1),
            callsOffered: 10, callsAnswered: 10));

        var response = await client.GetAsync("/api/analytics/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        // previousPeriodKpis should exist but be zeros (no data in prior period)
        json!["previousPeriodKpis"].Should().NotBeNull();
        json["previousPeriodKpis"]!["conversationsHandled"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task GetDashboard_ShouldReturnVolumeTrend_GroupedByHour()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        // Two snapshots in the same hour
        var hourBase = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(-2);
        await factory.SnapshotStore.UpsertAsync(MakeSnapshot(factory, "q1", hourBase.AddMinutes(0),
            callsAnswered: 15, serverId: "s1"));
        await factory.SnapshotStore.UpsertAsync(MakeSnapshot(factory, "q2", hourBase.AddMinutes(30),
            callsAnswered: 10, serverId: "s2"));

        var response = await client.GetAsync("/api/analytics/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var trend = json!["volumeTrend"]!.AsArray();
        trend.Count.Should().BeGreaterThan(0);
        // The hour bucket should sum both snapshots
        var hourBucket = trend.FirstOrDefault(t =>
            t!["label"]!.GetValue<string>().StartsWith(hourBase.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture), StringComparison.Ordinal));
        hourBucket.Should().NotBeNull();
        hourBucket!["value"]!.GetValue<double>().Should().Be(25);
    }

    [Fact]
    public async Task GetDashboard_ShouldFilterByQueue_WhenSpecified()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        await factory.SnapshotStore.UpsertAsync(MakeSnapshot(factory, "sales", now.AddHours(-1),
            callsAnswered: 30, serverId: "srv1"));
        await factory.SnapshotStore.UpsertAsync(MakeSnapshot(factory, "support", now.AddHours(-1),
            callsAnswered: 20, serverId: "srv2"));

        var response = await client.GetAsync("/api/analytics/dashboard?queue=sales");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["kpis"]!["conversationsHandled"]!.GetValue<int>().Should().Be(30);
    }

    // ─── CDR ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListCdr_ShouldReturnRows_WhenCdrExists()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await factory.CdrStore.UpsertAsync(MakeCdr(factory, "session-001", "support", "agent-1"));

        var response = await client.GetAsync("/api/analytics/cdr");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["data"]!.AsArray().Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ListCdr_ShouldEnrichAgentName_WhenAgentExists()
    {
        // When no agent record exists, agentId is used as fallback name.
        // With no real agent store seeded, agent name will be null or the raw id.
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await factory.CdrStore.UpsertAsync(MakeCdr(factory, "session-enrich", "support", agentId: null));

        var response = await client.GetAsync("/api/analytics/cdr");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var rows = json!["data"]!.AsArray();
        rows.Count.Should().BeGreaterThan(0);
        // agentName null when no agentId
        rows[0]!["agentName"].Should().BeNull();
    }

    [Fact]
    public async Task ListCdr_ShouldReturnAgentDisplayName_WhenAgentSeeded()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var agentId = EntityId.From("agent-enriched");
        await factory.AgentStore.SaveAsync(new Agent
        {
            AgentId = agentId,
            TenantId = new TenantId(UnifiedPlatformApiFactory.TestTenantId),
            UserId = EntityId.New(),
            DisplayName = "Alice Anderson",
            State = AgentState.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        await factory.CdrStore.UpsertAsync(MakeCdr(factory, "session-agent-enriched", "support", agentId.Value));

        var response = await client.GetAsync("/api/analytics/cdr");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var row = json!["data"]!.AsArray()
            .Single(r => r!["sessionId"]!.GetValue<string>() == "session-agent-enriched");
        row!["agentName"]!.GetValue<string>().Should().Be("Alice Anderson");
    }

    [Fact]
    public async Task ListCdr_ShouldEnrichBothRows_WhenTwoSessionsWithDistinctAgents()
    {
        // Two-session batch path (>1 id) — GetBySessionIdsAsync + GetByIdsAsync batch loads.
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        var agentA = EntityId.From("agent-A");
        var agentB = EntityId.From("agent-B");
        await factory.AgentStore.SaveAsync(new Agent
        {
            AgentId = agentA,
            TenantId = new TenantId(UnifiedPlatformApiFactory.TestTenantId),
            UserId = EntityId.New(),
            DisplayName = "Agent A",
            State = AgentState.Available,
            CreatedAt = now,
        }, CancellationToken.None);
        await factory.AgentStore.SaveAsync(new Agent
        {
            AgentId = agentB,
            TenantId = new TenantId(UnifiedPlatformApiFactory.TestTenantId),
            UserId = EntityId.New(),
            DisplayName = "Agent B",
            State = AgentState.Available,
            CreatedAt = now,
        }, CancellationToken.None);

        var cdrA = MakeCdr(factory, "session-two-a", "support", agentA.Value);
        cdrA.StartedAt = now.AddMinutes(-2);
        cdrA.CompletedAt = now.AddMinutes(-2).AddMinutes(5);
        var cdrB = MakeCdr(factory, "session-two-b", "support", agentB.Value);
        cdrB.StartedAt = now.AddMinutes(-1);
        cdrB.CompletedAt = now.AddMinutes(-1).AddMinutes(5);
        await factory.CdrStore.UpsertAsync(cdrA);
        await factory.CdrStore.UpsertAsync(cdrB);

        var response = await client.GetAsync("/api/analytics/cdr");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var rows = json!["data"]!.AsArray();
        rows.Single(r => r!["sessionId"]!.GetValue<string>() == "session-two-a")!["agentName"]!
            .GetValue<string>().Should().Be("Agent A");
        rows.Single(r => r!["sessionId"]!.GetValue<string>() == "session-two-b")!["agentName"]!
            .GetValue<string>().Should().Be("Agent B");
    }

    [Fact]
    public async Task ListQa_ShouldEnrichBothRows_WhenTwoSessionsWithLinkedCdrs()
    {
        // Two-session batch path (>1 id) for ListQa — GetBySessionIdsAsync CDR batch load.
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await factory.CdrStore.UpsertAsync(MakeCdr(factory, "qa-two-a", "sales"));
        await factory.CdrStore.UpsertAsync(MakeCdr(factory, "qa-two-b", "billing"));
        await factory.QaStore.SaveAsync(MakeQaResult(factory, "qa-two-a"));
        await factory.QaStore.SaveAsync(MakeQaResult(factory, "qa-two-b"));

        var response = await client.GetAsync("/api/analytics/qa");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var rows = json!["data"]!.AsArray();
        rows.Single(r => r!["sessionId"]!.GetValue<string>() == "qa-two-a")!["queueName"]!
            .GetValue<string>().Should().Be("sales");
        rows.Single(r => r!["sessionId"]!.GetValue<string>() == "qa-two-b")!["queueName"]!
            .GetValue<string>().Should().Be("billing");
    }

    [Fact]
    public async Task ListCdr_ShouldDefaultChannelToVoice_WhenNoConversation()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await factory.CdrStore.UpsertAsync(MakeCdr(factory, "session-voice", "support"));

        var response = await client.GetAsync("/api/analytics/cdr");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var row = json!["data"]!.AsArray()[0];
        row!["channel"]!.GetValue<string>().Should().Be("voice");
    }

    [Fact]
    public async Task ListCdr_ShouldComputeSlaMet_WhenWaitUnderThreshold()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var cdr = MakeCdr(factory, "session-sla-met", "support");
        cdr.WaitTimeMs = 5000; // 5 seconds — under the 20s default
        await factory.CdrStore.UpsertAsync(cdr);

        var response = await client.GetAsync("/api/analytics/cdr");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var row = json!["data"]!.AsArray()[0];
        row!["slaMet"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ListCdr_ShouldPaginate_WithHasMoreFlag()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        // Seed 3 CDR rows
        for (var i = 1; i <= 3; i++)
        {
            var cdr = MakeCdr(factory, $"session-page-{i}", "support");
            cdr.StartedAt = now.AddMinutes(-i);
            cdr.CompletedAt = now.AddMinutes(-i).AddMinutes(5);
            await factory.CdrStore.UpsertAsync(cdr);
        }

        var response = await client.GetAsync("/api/analytics/cdr?page=1&pageSize=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["data"]!.AsArray().Count.Should().Be(2);
        json["hasMore"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ListCdr_ShouldFilterByDateRange_WhenSpecified()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        var recent = MakeCdr(factory, "session-recent", "support");
        recent.StartedAt = now.AddHours(-1);
        recent.CompletedAt = now.AddHours(-1).AddMinutes(5);
        await factory.CdrStore.UpsertAsync(recent);

        var old = MakeCdr(factory, "session-old", "support");
        old.StartedAt = now.AddDays(-30);
        old.CompletedAt = now.AddDays(-30).AddMinutes(5);
        await factory.CdrStore.UpsertAsync(old);

        var from = now.AddDays(-2).ToString("O");
        var to = now.ToString("O");
        var response = await client.GetAsync($"/api/analytics/cdr?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var rows = json!["data"]!.AsArray();
        rows.Count.Should().Be(1);
        rows[0]!["sessionId"]!.GetValue<string>().Should().Be("session-recent");
    }

    [Fact]
    public async Task GetCdrDetail_ShouldReturnDetail_WhenExists()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await factory.CdrStore.UpsertAsync(MakeCdr(factory, "session-detail", "support"));

        var response = await client.GetAsync("/api/analytics/cdr/session-detail");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["cdr"]!["sessionId"]!.GetValue<string>().Should().Be("session-detail");
        json["timeline"]!.AsArray().Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetCdrDetail_ShouldReturn404_WhenNotFound()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/analytics/cdr/nonexistent-session");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── QA ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListQa_ShouldReturnNormalizedScores_WhenResultsExist()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await factory.QaStore.SaveAsync(MakeQaResult(factory, "qa-session-1",
            qaScore: 0.8, maxScore: 1.0)); // 80%

        var response = await client.GetAsync("/api/analytics/qa");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var rows = json!["data"]!.AsArray();
        rows.Count.Should().Be(1);
        rows[0]!["qaScore"]!.GetValue<double>().Should().BeApproximately(80.0, 0.1);
    }

    [Fact]
    public async Task ListQa_ShouldEnrichFromCdr_WhenLinkedSessionExists()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        // Seed matching CDR row
        await factory.CdrStore.UpsertAsync(MakeCdr(factory, "qa-linked-session", "billing"));
        await factory.QaStore.SaveAsync(MakeQaResult(factory, "qa-linked-session"));

        var response = await client.GetAsync("/api/analytics/qa");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var row = json!["data"]!.AsArray()[0];
        row!["queueName"]!.GetValue<string>().Should().Be("billing");
    }

    [Fact]
    public async Task ListQa_ShouldMapSentimentLabel_WhenSentimentExists()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await factory.QaStore.SaveAsync(MakeQaResult(factory, "qa-sentiment",
            sentimentLabel: SentimentLabel.Positive));

        var response = await client.GetAsync("/api/analytics/qa");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var row = json!["data"]!.AsArray()[0];
        row!["sentimentLabel"]!.GetValue<string>().Should().Be("Positive");
    }

    [Fact]
    public async Task ListQa_ShouldReturnTopics_WhenClassified()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await factory.QaStore.SaveAsync(MakeQaResult(factory, "qa-topics",
            primaryTopic: "Billing", topicNames: ["Billing", "Refund"]));

        var response = await client.GetAsync("/api/analytics/qa");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var row = json!["data"]!.AsArray()[0];
        var topics = row!["topics"]!.AsArray();
        topics.Count.Should().Be(2);
        topics.Select(t => t!.GetValue<string>()).Should().Contain("Billing");
    }

    [Fact]
    public async Task GetQaDetail_ShouldReturnFullDetail_WhenExists()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await factory.QaStore.SaveAsync(MakeQaResult(factory, "qa-detail-session"));

        var response = await client.GetAsync("/api/analytics/qa/qa-detail-session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["sessionId"]!.GetValue<string>().Should().Be("qa-detail-session");
    }

    [Fact]
    public async Task GetQaDetail_ShouldIncludeComplianceViolations_WhenPresent()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var result = MakeQaResult(factory, "qa-violations") with
        {
            ComplianceViolations =
            [
                new ComplianceViolation
                {
                    RuleId = "rule-1",
                    RuleName = "PCI Compliance",
                    Severity = ComplianceSeverity.Critical,
                    Description = "Card number mentioned in clear",
                    Evidence = "4111...",
                },
            ],
        };
        await factory.QaStore.SaveAsync(result);

        var response = await client.GetAsync("/api/analytics/qa/qa-violations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var violations = json!["violations"]!.AsArray();
        violations.Count.Should().Be(1);
        violations[0]!["ruleName"]!.GetValue<string>().Should().Be("PCI Compliance");
        violations[0]!["severity"]!.GetValue<string>().Should().Be("Critical");
    }

    [Fact]
    public async Task GetQaDetail_ShouldIncludeCriteria_WhenScored()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await factory.QaStore.SaveAsync(MakeQaResult(factory, "qa-criteria",
            qaScore: 0.75, maxScore: 1.0, includeItems: true));

        var response = await client.GetAsync("/api/analytics/qa/qa-criteria");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var criteria = json!["criteria"]!.AsArray();
        criteria.Count.Should().BeGreaterThan(0);
        criteria[0]!["category"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetQaDetail_ShouldReturn404_WhenNotFound()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/analytics/qa/nonexistent-qa");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Intervals ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListIntervals_ShouldReturnSnapshots_WhenExist()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        await factory.SnapshotStore.UpsertAsync(MakeSnapshot(factory, "support", now.AddHours(-1),
            callsOffered: 10, callsAnswered: 8));

        var response = await client.GetAsync("/api/analytics/intervals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var items = json!.AsArray();
        items.Count.Should().BeGreaterThan(0);
        items[0]!["queueName"]!.GetValue<string>().Should().Be("support");
        items[0]!["callsAnswered"]!.GetValue<int>().Should().Be(8);
    }

    [Fact]
    public async Task ListIntervals_ShouldFilterByQueue_WhenSpecified()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;

        await factory.SnapshotStore.UpsertAsync(MakeSnapshot(factory, "sales", now.AddHours(-1),
            callsAnswered: 20, serverId: "s1"));
        await factory.SnapshotStore.UpsertAsync(MakeSnapshot(factory, "support", now.AddHours(-1),
            callsAnswered: 15, serverId: "s2"));

        var response = await client.GetAsync("/api/analytics/intervals?queue=sales");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var items = json!.AsArray();
        items.Count.Should().Be(1);
        items[0]!["queueName"]!.GetValue<string>().Should().Be("sales");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static IntervalSnapshot MakeSnapshot(
        UnifiedPlatformApiFactory factory,
        string queueName,
        DateTimeOffset intervalStart,
        int callsOffered = 10,
        int callsAnswered = 8,
        int callsAbandoned = 1,
        int slaMetCount = 7,
        long totalWaitMs = 16_000,
        string serverId = "server-1")
    {
        _ = factory; // factory parameter kept for call-site consistency
        return new IntervalSnapshot
        {
            TenantId = UnifiedPlatformApiFactory.TestTenantId,
            QueueName = queueName,
            ServerId = serverId,
            IntervalStart = intervalStart,
            IntervalSeconds = 1800,
            CallsOffered = callsOffered,
            CallsAnswered = callsAnswered,
            CallsAbandoned = callsAbandoned,
            SlaMetCount = slaMetCount,
            TotalWaitMs = totalWaitMs,
        };
    }

    private static CompletedSessionRow MakeCdr(
        UnifiedPlatformApiFactory factory,
        string sessionId,
        string queueName = "support",
        string? agentId = "agent-1")
    {
        _ = factory; // factory parameter kept for call-site consistency
        var now = DateTimeOffset.UtcNow;
        return new CompletedSessionRow
        {
            TenantId = UnifiedPlatformApiFactory.TestTenantId,
            SessionId = sessionId,
            ServerId = "server-1",
            Direction = 0,
            CallerIdNum = "+15551234567",
            QueueName = queueName,
            AgentId = agentId,
            StartedAt = now.AddHours(-1),
            ConnectedAt = now.AddHours(-1).AddSeconds(10),
            CompletedAt = now.AddHours(-1).AddMinutes(5),
            DurationMs = 300_000,
            TalkTimeMs = 280_000,
            WaitTimeMs = 10_000,
            HoldTimeMs = 0,
            HangupCause = 16,
            FinalState = 8,
            CreatedAt = now.AddHours(-1),
        };
    }

    private static CallAnalysisResult MakeQaResult(
        UnifiedPlatformApiFactory factory,
        string sessionId,
        double qaScore = 0.85,
        double maxScore = 1.0,
        SentimentLabel? sentimentLabel = null,
        string? primaryTopic = null,
        string[]? topicNames = null,
        bool includeItems = false)
    {
        _ = factory; // factory parameter kept for call-site consistency
        var items = includeItems
            ? (IReadOnlyList<QaItemResult>)
            [
                new QaItemResult
                {
                    ItemId = "item-1",
                    Category = "Greeting",
                    Score = 0.9,
                    Weight = 1.0,
                    Passed = true,
                    Feedback = "Good opening",
                },
            ]
            : Array.Empty<QaItemResult>();

        SentimentResult? sentiment = sentimentLabel.HasValue
            ? new SentimentResult
            {
                OverallScore = 0.7f,
                OverallLabel = sentimentLabel.Value,
                StartLabel = sentimentLabel.Value,
                EndLabel = sentimentLabel.Value,
                Trend = SentimentTrend.Stable,
            }
            : null;

        TopicResult? topics = primaryTopic is not null
            ? new TopicResult
            {
                PrimaryTopic = primaryTopic,
                AllTopics = (topicNames ?? [primaryTopic]).Select((n, i) =>
                    new TopicMatch { TopicId = $"topic-{i}", TopicName = n, Confidence = 0.9f }).ToList(),
            }
            : null;

        return new CallAnalysisResult
        {
            SessionId = sessionId,
            TenantId = UnifiedPlatformApiFactory.TestTenantId,
            AnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ProcessingTime = TimeSpan.FromSeconds(2),
            QualityScore = new QaResult
            {
                TotalScore = qaScore * maxScore,
                MaxPossibleScore = maxScore,
                Items = items,
            },
            Sentiment = sentiment,
            Topics = topics,
            Metrics = new ConversationMetrics
            {
                AgentTalkRatio = 0.6,
                TotalAgentTalk = TimeSpan.FromSeconds(180),
                TotalCallerTalk = TimeSpan.FromSeconds(120),
                LongestAgentMonologue = TimeSpan.FromSeconds(60),
                LongestCallerMonologue = TimeSpan.FromSeconds(45),
                AgentTurnCount = 12,
                CallerTurnCount = 10,
                SilenceCount = 3,
                TotalSilence = TimeSpan.FromSeconds(15),
                InterruptionCount = 2,
            },
        };
    }
}
