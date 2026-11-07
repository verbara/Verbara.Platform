using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Verbara.Platform.Api.Serialization;
using Verbara.Sdk.Pro.CallAnalytics.Domain;
using Verbara.Sdk.Pro.EventStore;

namespace Verbara.Platform.Api.Tests.CallRecords;

/// <summary>
/// call-records-are-written-once, spec <c>call-record-read-contract</c>: the CDR list and detail answer the wire shapes of
/// the change's cross-repo fixtures (impact.yaml <c>fixtures:</c>, copied verbatim into <c>CallRecords/Fixtures/</c>), which
/// Platform.Web reads. Rows shaped like the fixtures are served by the real host, whose JSON options carry
/// <see cref="ApiJsonContext"/>, and each response is compared member by member: the same names, the same JSON types, and
/// the same absences (a null member is not written). A record's <c>sessionId</c> is its first event's Sdk session id, 32
/// lowercase hex characters usable unencoded in a path.
/// </summary>
public sealed partial class CallRecordReadContractTests
{
    private const string ListPath = "/api/v1/analytics/cdr";

    // The ids of the fixtures themselves, so the served rows carry exactly the values the console was written against.
    private const string AnsweredId = "c7a1f0e25b6d4c3e9a8f1e2d3c4b5a69";
    private const string UnansweredId = "0b9e4d217c3a4f58b6e192d4a7c0f3e8";
    private const string PartialId = "5d2a8c419e074b3fa1c67f80e2d94b15";
    private const string OlderId = "9e4b0c7d1a2f4e3b8c5d6a7f0e1b2c3d";

    private static readonly string[] PopulatedFixtures =
    [
        "cdr-list-response.v1.json",
        "cdr-list-response.partial.v1.json",
        "cdr-detail-response.v1.json",
        "cdr-detail-response.sparse.v1.json",
        "cdr-detail-response.partial.v1.json",
    ];

    private static readonly string[] DetailFixtures =
    [
        "cdr-detail-response.v1.json",
        "cdr-detail-response.sparse.v1.json",
        "cdr-detail-response.partial.v1.json",
    ];

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex SdkSessionId();

    // ─── The list ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListCdr_ShouldMatchTheListFixture_WhenAPageHasAnAnsweredAndAnUnansweredCall()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;
        await factory.CdrStore.UpsertAsync(AnsweredRow(AnsweredId, now.AddMinutes(-10)));
        await factory.CdrStore.UpsertAsync(UnansweredRow(UnansweredId, now.AddMinutes(-20)));
        await factory.CdrStore.UpsertAsync(UnansweredRow(OlderId, now.AddMinutes(-30)));
        await factory.QaStore.SaveAsync(ScoredAnalysis(AnsweredId));

        var body = await GetObjectAsync(client, $"{ListPath}?page=1&pageSize=2");

        var fixture = Fixture("cdr-list-response.v1.json");
        AssertSameShape(fixture, body);
        body["hasMore"]!.GetValue<bool>().Should().BeTrue("a third record lies past the page of two");
        body["page"]!.GetValue<int>().Should().Be(1);
        body["pageSize"]!.GetValue<int>().Should().Be(2);
        SessionIdsOf(body).Should().Equal(AnsweredId, UnansweredId);
        AssertEverySessionIdIsAnSdkSessionId(body);
    }

    [Fact]
    public async Task ListCdr_ShouldEqualTheEmptyFixture_WhenTheTenantHasNoRecord()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var body = await GetObjectAsync(client, ListPath);

        JsonNode.DeepEquals(body, Fixture("cdr-list-response.empty.v1.json")).Should().BeTrue(
            $"an empty list is the empty fixture exactly, not an error and not another shape; got {body.ToJsonString()}");
    }

    [Fact]
    public async Task ListCdr_ShouldMatchThePartialListFixture_WhenTheRecordIsPartial()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.CdrStore.UpsertAsync(AnsweredRow(PartialId, DateTimeOffset.UtcNow.AddMinutes(-10), partial: true));
        await factory.QaStore.SaveAsync(ScoredAnalysis(PartialId));

        var body = await GetObjectAsync(client, ListPath);

        AssertSameShape(Fixture("cdr-list-response.partial.v1.json"), body);
        body["data"]![0]!["isPartial"]!.GetValue<bool>().Should().BeTrue();
        AssertEverySessionIdIsAnSdkSessionId(body);
    }

    // ─── The detail ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCdrDetail_ShouldMatchTheDetailFixture_WhenTheCallWasAnsweredTransferredScoredAndRecorded()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.CdrStore.UpsertAsync(AnsweredRow(AnsweredId, DateTimeOffset.UtcNow.AddMinutes(-10)));
        await factory.QaStore.SaveAsync(ScoredAnalysis(AnsweredId));

        var body = await GetObjectAsync(client, $"{ListPath}/{AnsweredId}");

        AssertSameShape(Fixture("cdr-detail-response.v1.json"), body);
        TimelineEvents(body).Should().Equal("started", "answered", "transferred", "ended");
        AssertEverySessionIdIsAnSdkSessionId(body);
        AssertStreamUrlCarriesTheSessionId(body, AnsweredId);
    }

    [Fact]
    public async Task GetCdrDetail_ShouldMatchTheSparseFixture_WhenTheCallWasUnansweredUnscoredAndUnrecorded()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.CdrStore.UpsertAsync(UnansweredRow(UnansweredId, DateTimeOffset.UtcNow.AddMinutes(-10)));

        var body = await GetObjectAsync(client, $"{ListPath}/{UnansweredId}");

        AssertSameShape(Fixture("cdr-detail-response.sparse.v1.json"), body);
        body["transferCount"]!.GetValue<int>().Should().Be(0);
        body["hasTranscript"]!.GetValue<bool>().Should().BeFalse();
        TimelineEvents(body).Should().Equal("started", "ended");
        AssertEverySessionIdIsAnSdkSessionId(body);
    }

    [Fact]
    public async Task GetCdrDetail_ShouldMatchThePartialDetailFixture_WhenTheRecordIsPartial()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.CdrStore.UpsertAsync(AnsweredRow(PartialId, DateTimeOffset.UtcNow.AddMinutes(-10), partial: true));
        await factory.QaStore.SaveAsync(ScoredAnalysis(PartialId));

        var body = await GetObjectAsync(client, $"{ListPath}/{PartialId}");

        AssertSameShape(Fixture("cdr-detail-response.partial.v1.json"), body);
        body["cdr"]!["isPartial"]!.GetValue<bool>().Should().BeTrue();
        body["cdr"]!["holdCount"]!.GetValue<int>().Should().Be(2);
        AssertEverySessionIdIsAnSdkSessionId(body);
        AssertStreamUrlCarriesTheSessionId(body, PartialId);
    }

    [Fact]
    public async Task GetCdrDetail_ShouldAnswer404WithNoBody_WhenTheTenantHasNoSuchRecord()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        await factory.CdrStore.UpsertAsync(AnsweredRow(AnsweredId, DateTimeOffset.UtcNow.AddMinutes(-10)));

        using var response = await client.GetAsync($"{ListPath}/{UnansweredId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty("a missing record answers 404 with no body");
    }

    // ─── Through ApiJsonContext itself ────────────────────────────────────────

    [Fact]
    public void ApiJsonContext_ShouldRoundTripEveryFixtureUnchanged_WhenItReadsAndWritesTheCdrDtos()
    {
        foreach (var name in new[] { "cdr-list-response.v1.json", "cdr-list-response.empty.v1.json", "cdr-list-response.partial.v1.json" })
        {
            var fixture = Fixture(name);
            var dto = JsonSerializer.Deserialize(fixture.ToJsonString(), ApiJsonContext.Default.PagedDataResponseCdrRowDto)!;
            var written = JsonNode.Parse(JsonSerializer.Serialize(dto, ApiJsonContext.Default.PagedDataResponseCdrRowDto))!;
            JsonNode.DeepEquals(written, fixture).Should().BeTrue(
                $"{name} is exactly what ApiJsonContext writes for its DTO; got {written.ToJsonString()}");
        }

        foreach (var name in DetailFixtures)
        {
            var fixture = Fixture(name);
            var dto = JsonSerializer.Deserialize(fixture.ToJsonString(), ApiJsonContext.Default.CdrDetailDto)!;
            var written = JsonNode.Parse(JsonSerializer.Serialize(dto, ApiJsonContext.Default.CdrDetailDto))!;
            JsonNode.DeepEquals(written, fixture).Should().BeTrue(
                $"{name} is exactly what ApiJsonContext writes for its DTO; got {written.ToJsonString()}");
        }
    }

    // ─── The fixture files themselves ─────────────────────────────────────────

    [Fact]
    public void Fixtures_ShouldCarryOnlySdkSessionIds_AndEachDetailsRecordingShouldBeNamedByItsSessionId()
    {
        foreach (var name in PopulatedFixtures)
        {
            var ids = SessionIdNodes(Fixture(name)).Select(n => n.GetValue<string>()).ToList();
            ids.Should().NotBeEmpty($"{name} is a populated fixture");
            ids.Should().OnlyContain(id => SdkSessionId().IsMatch(id),
                $"every sessionId in {name} is an Sdk session id, 32 lowercase hex characters");
        }

        foreach (var name in DetailFixtures)
        {
            var detail = Fixture(name);
            var sessionId = detail["cdr"]!["sessionId"]!.GetValue<string>();
            if (detail["recordingName"] is { } recordingName)
            {
                Path.GetFileNameWithoutExtension(recordingName.GetValue<string>()).Should().Be(sessionId,
                    $"{name}'s recordingName stem is its sessionId");
                AssertStreamUrlCarriesTheSessionId(detail, sessionId);
            }
            else
            {
                detail.ContainsKey("recordingStreamUrl").Should().BeFalse($"{name} has no recording, so no stream link");
            }
        }
    }

    // ─── Rows shaped like the fixtures ────────────────────────────────────────

    private static CompletedSessionRow AnsweredRow(string sessionId, DateTimeOffset startedAt, bool partial = false) => new()
    {
        IsPartial = partial,
        PartialCause = partial ? "leader_change" : null,
        TenantId = UnifiedPlatformApiFactory.TestTenantId,
        SessionId = sessionId,
        LinkedSessionId = "1730732531.42",
        ServerId = "server-1",
        Direction = 0,
        CallerIdNum = "55502520001",
        Extension = "800",
        QueueName = "support",
        AgentId = "agent-1001",
        StartedAt = startedAt,
        ConnectedAt = startedAt.AddMilliseconds(7638),
        CompletedAt = startedAt.AddMilliseconds(82422),
        DurationMs = 82422,
        TalkTimeMs = 74784,
        WaitTimeMs = 6210,
        HoldTimeMs = 0,
        HoldCount = (short)(partial ? 2 : 0),
        HangupCause = 16,
        FinalState = 8,
        TransferredTo = "1002",
        TransferType = 1,
        TransferCount = 1,
        HangupSource = 1,
        WrapUpDurationMs = 15000,
        RecordingName = $"{sessionId}.wav",
        Metadata = new Dictionary<string, string> { ["x-lab-tag"] = "sample" },
        CreatedAt = startedAt,
    };

    private static CompletedSessionRow UnansweredRow(string sessionId, DateTimeOffset startedAt) => new()
    {
        TenantId = UnifiedPlatformApiFactory.TestTenantId,
        SessionId = sessionId,
        ServerId = "server-1",
        Direction = 0,
        CallerIdNum = "55502520002",
        QueueName = "support",
        StartedAt = startedAt,
        CompletedAt = startedAt.AddMilliseconds(30005),
        DurationMs = 30005,
        WaitTimeMs = 30005,
        HoldTimeMs = 0,
        HangupCause = 19,
        FinalState = 0,
        CreatedAt = startedAt,
    };

    private static CallAnalysisResult ScoredAnalysis(string sessionId) => new()
    {
        SessionId = sessionId,
        TenantId = UnifiedPlatformApiFactory.TestTenantId,
        AnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        ProcessingTime = TimeSpan.FromSeconds(2),
        QualityScore = new QaResult { TotalScore = 87.5, MaxPossibleScore = 100, Items = [] },
        Sentiment = new SentimentResult
        {
            OverallScore = 0.7f,
            OverallLabel = SentimentLabel.Positive,
            StartLabel = SentimentLabel.Neutral,
            EndLabel = SentimentLabel.Positive,
            Trend = SentimentTrend.Improving,
        },
        Summary = new CallSummary
        {
            Reason = "Billing question",
            Outcome = "Resolved",
            Narrative = "The caller asked about an invoice; the agent explained the charge.",
        },
        Metrics = new ConversationMetrics
        {
            AgentTalkRatio = 0.6,
            TotalAgentTalk = TimeSpan.FromSeconds(45),
            TotalCallerTalk = TimeSpan.FromSeconds(30),
            LongestAgentMonologue = TimeSpan.FromSeconds(20),
            LongestCallerMonologue = TimeSpan.FromSeconds(15),
            AgentTurnCount = 6,
            CallerTurnCount = 5,
            SilenceCount = 1,
            TotalSilence = TimeSpan.FromSeconds(3),
            InterruptionCount = 0,
        },
    };

    // ─── Comparison ───────────────────────────────────────────────────────────

    private static JsonObject Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "CallRecords", "Fixtures", name)))!.AsObject();

    private static async Task<JsonObject> GetObjectAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>
    /// Asserts that <paramref name="actual"/> has exactly the members of <paramref name="expected"/> (so a renamed member,
    /// a member written as null and a member the fixture omits all fail), each with the fixture's JSON type; objects are
    /// compared the same way, and arrays element by element with the same length. Values are not compared: a timestamp
    /// differs by nature.
    /// </summary>
    private static void AssertSameShape(JsonNode expected, JsonNode? actual, string path = "$")
    {
        actual.Should().NotBeNull($"{path} is written");
        actual!.GetValueKind().Should().Be(expected.GetValueKind(), $"{path} keeps the fixture's JSON type");
        switch (expected)
        {
            case JsonObject wantObject:
                var got = actual.AsObject();
                got.Select(kv => kv.Key).Should().BeEquivalentTo(
                    wantObject.Select(kv => kv.Key), $"{path} has exactly the fixture's members");
                foreach (var (name, want) in wantObject)
                {
                    want.Should().NotBeNull($"{path}.{name}: a fixture never writes a null member");
                    AssertSameShape(want!, got[name], $"{path}.{name}");
                }

                break;
            case JsonArray wantArray:
                var gotArray = actual.AsArray();
                gotArray.Count.Should().Be(wantArray.Count, $"{path} has the fixture's number of elements");
                for (var i = 0; i < wantArray.Count; i++)
                {
                    AssertSameShape(wantArray[i]!, gotArray[i], $"{path}[{i}]");
                }

                break;
        }
    }

    private static List<string> SessionIdsOf(JsonObject list) =>
        list["data"]!.AsArray().Select(r => r!["sessionId"]!.GetValue<string>()).ToList();

    private static List<string> TimelineEvents(JsonObject detail) =>
        detail["timeline"]!.AsArray().Select(e => e!["event"]!.GetValue<string>()).ToList();

    private static IEnumerable<JsonNode> SessionIdNodes(JsonObject body) =>
        body["data"] is JsonArray rows
            ? rows.Select(r => r!["sessionId"]!)
            : [body["cdr"]!["sessionId"]!];

    private static void AssertEverySessionIdIsAnSdkSessionId(JsonObject body)
    {
        var ids = SessionIdNodes(body).Select(n => n.GetValue<string>()).ToList();
        ids.Should().NotBeEmpty();
        ids.Should().OnlyContain(id => SdkSessionId().IsMatch(id),
            "a record's sessionId is its first event's Sdk session id, 32 lowercase hex characters, never a composite id");
    }

    private static void AssertStreamUrlCarriesTheSessionId(JsonObject detail, string sessionId)
    {
        var url = detail["recordingStreamUrl"]!.GetValue<string>();
        url.Should().NotContain("#", "the stream link carries no fragment");
        url.Should().NotContain("?", "the stream link carries no query string");
        var segments = url.Split('/');
        segments.Should().HaveCountGreaterThanOrEqualTo(2);
        segments[^1].Should().Be("stream");
        segments[^2].Should().Be(sessionId, "the stream link carries the record's sessionId unencoded as a path segment");
        Uri.EscapeDataString(segments[^2]).Should().Be(segments[^2], "the sessionId needs no encoding in a path");
    }
}
