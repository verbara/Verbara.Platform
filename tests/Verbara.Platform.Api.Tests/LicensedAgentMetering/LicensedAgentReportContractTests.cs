using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Queues.Licensing;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests.LicensedAgentMetering;

/// <summary>
/// licensed-agent-metering slice 3 (tasks.md 7.1; licensed-agent-reporting): the peaks report and the export,
/// built by the production mapping from the rows each fixture describes and serialised through
/// <see cref="ApiJsonContext"/>, are the frozen fixtures field for field — the same names, the same JSON
/// types, the same values, nothing more. Instants compare as instants: the fixtures write them without
/// fractional digits, the API in the canonical form of the hash chain (seven digits and <c>Z</c>).
/// </summary>
public sealed class LicensedAgentReportContractTests
{
    private static JsonObject Fixture(string name)
    {
        var path = Path.Join(AppContext.BaseDirectory, "LicensedAgentMetering", "Fixtures", name);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove("_comment");
        return node;
    }

    private static JsonNode Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, info))!;

    private static DateOnly Day(JsonNode? node) =>
        DateOnly.ParseExact(node!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset Instant(JsonNode? node) =>
        DateTimeOffset.Parse(node!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static string? Text(JsonNode? node) => node?.GetValue<string>();

    private static LicensedAgentLicenseDto LicenseOf(JsonNode license) => new(
        Text(license["licenseId"]),
        Text(license["licensee"]),
        Text(license["tier"])!,
        license["maxAgents"]?.GetValue<int>(),
        license["maxAgentsAdvisory"]!.GetValue<bool>());

    private static List<LicenseAgentChainHead> HeadsOf(JsonNode heads) =>
        heads.AsArray().Select(h => new LicenseAgentChainHead(
            Text(h!["tenantId"]), h["headSequence"]!.GetValue<long>(), Text(h["headHash"])!, Text(h["licenseId"]))).ToList();

    private static LicenseAgentDaily DailyRow(string? tenantId, DateOnly day, int revision, int agents, DateTimeOffset closedAt, string rowHash) => new()
    {
        TenantId = tenantId,
        Sequence = 1,
        Day = day,
        Revision = revision,
        LicensedAgents = agents,
        ClosedAt = closedAt,
        ClosedThroughSequence = 0,
        LicenseId = null,
        PrevHash = "lac1:prev",
        RowHash = rowHash,
    };

    [Fact]
    public void Peaks_ShouldMatchTheFrozenFixture_FieldForField()
    {
        var fixture = Fixture("licensed-agent-peaks.v1.json");
        var rows = new List<LicenseAgentDaily>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var day in fixture["days"]!.AsArray())
        {
            var d = Day(day!["day"]);
            rows.Add(DailyRow(null, d, 0, day["deploymentLicensedAgents"]!.GetValue<int>(), Instant(day["tenants"]![0]!["closedAt"]),
                Text(day["deploymentRowHash"])!));
            foreach (var tenant in day["tenants"]!.AsArray())
            {
                var revision = tenant!["revision"]!.GetValue<int>();
                var agents = tenant["licensedAgents"]!.GetValue<int>();
                rows.Add(DailyRow(Text(tenant["tenantId"]), d, revision, agents, Instant(tenant["closedAt"]), Text(tenant["rowHash"])!));
                // A superseded revision the report must not show.
                if (revision > 0)
                    rows.Add(DailyRow(Text(tenant["tenantId"]), d, revision - 1, agents - 1, Instant(tenant["closedAt"]).AddHours(-9), "lac1:superseded"));
                names[Text(tenant["tenantId"])!] = Text(tenant["tenantName"])!;
            }
        }

        rows.Reverse(); // the mapping must not depend on the order the store returns rows in
        var response = LicensedAgentReports.BuildPeaks(
            Day(fixture["from"]), Day(fixture["to"]),
            new LicensedAgentPeaksData(Text(fixture["dayZone"])!, rows, names, HeadsOf(fixture["chainHeads"]!)),
            LicenseOf(fixture["license"]!));

        AssertMatches(fixture, Serialize(response, ApiJsonContext.Default.LicensedAgentPeaksResponse), "$");
    }

    [Fact]
    public void Export_ShouldMatchTheFrozenFixture_FieldForField()
    {
        var fixture = Fixture("licensed-agent-export.v1.json");
        var events = fixture["events"]!.AsArray().Select(e => new LicenseAgentEvent
        {
            TenantId = Text(e!["tenantId"]),
            Sequence = e["sequence"]!.GetValue<long>(),
            EventId = Guid.Parse(Text(e["eventId"])!, CultureInfo.InvariantCulture),
            Kind = Text(e["kind"])!,
            OccurredAt = Instant(e["occurredAt"]),
            AgentId = Text(e["agentId"]),
            UserId = Text(e["userId"]),
            ActorUserId = Text(e["actorUserId"]),
            ConversationId = Text(e["conversationId"]),
            UserStatus = Text(e["userStatus"]),
            Counted = e["counted"]?.GetValue<bool>(),
            LicenseId = Text(e["licenseId"]),
            PrevHash = Text(e["prevHash"])!,
            RowHash = Text(e["rowHash"])!,
        }).ToList();
        var daily = fixture["daily"]!.AsArray().Select(d => new LicenseAgentDaily
        {
            TenantId = Text(d!["tenantId"]),
            Sequence = d["sequence"]!.GetValue<long>(),
            Day = Day(d["day"]),
            Revision = d["revision"]!.GetValue<int>(),
            LicensedAgents = d["licensedAgents"]!.GetValue<int>(),
            ClosedAt = Instant(d["closedAt"]),
            ClosedThroughSequence = d["closedThroughSequence"]!.GetValue<long>(),
            LicenseId = Text(d["licenseId"]),
            PrevHash = Text(d["prevHash"])!,
            RowHash = Text(d["rowHash"])!,
        }).ToList();

        events.Reverse();
        daily.Reverse();
        var response = LicensedAgentReports.BuildExport(
            Day(fixture["from"]), Day(fixture["to"]),
            new LicensedAgentExportData(Text(fixture["dayZone"])!, events, daily, HeadsOf(fixture["chainHeads"]!)),
            LicenseOf(fixture["license"]!),
            Instant(fixture["generatedAt"]));

        AssertMatches(fixture, Serialize(response, ApiJsonContext.Default.LicensedAgentExportResponse), "$");
    }

    [Fact]
    public void Peaks_ShouldWritePeakDayAsNull_WhenNoDayOfTheRangeIsClosed()
    {
        var response = LicensedAgentReports.BuildPeaks(
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31),
            new LicensedAgentPeaksData("UTC", [], new Dictionary<string, string>(), []),
            new LicensedAgentLicenseDto(null, null, "None", null, true));

        var json = Serialize(response, ApiJsonContext.Default.LicensedAgentPeaksResponse);

        var deployment = json["deployment"]!.AsObject();
        deployment.ContainsKey("peakDay").Should().BeTrue("peakDay is string|null on the wire, never omitted");
        deployment["peakDay"].Should().BeNull();
        deployment["peakLicensedAgents"]!.GetValue<int>().Should().Be(0);
        deployment["overBand"]!.GetValue<bool>().Should().BeFalse();
        json["days"]!.AsArray().Should().BeEmpty();
        var license = json["license"]!.AsObject();
        license.Select(kv => kv.Key).Should().Equal("licenseId", "licensee", "tier", "maxAgents", "maxAgentsAdvisory");
        license["licenseId"].Should().BeNull();
        license["maxAgents"].Should().BeNull();
    }

    [Fact]
    public void License_ShouldReadTheLoadedLicence_AndReportMaxAgentsAsAdvisory()
    {
        var status = Substitute.For<ILicenseStatus>();
        status.LicenseId.Returns("lic-7f3c2a91");
        status.Licensee.Returns("Example Contact Ltd");
        status.Tier.Returns(LicenseTier.SelfHostBusiness);
        status.MaxAgents.Returns(50);

        LicensedAgentReports.License(status).Should().Be(
            new LicensedAgentLicenseDto("lic-7f3c2a91", "Example Contact Ltd", "SelfHostBusiness", 50, true));

        string? none = null;
        status.LicenseId.Returns(none);
        status.Licensee.Returns(none);
        status.Tier.Returns(LicenseTier.None);
        status.MaxAgents.Returns(0);
        LicensedAgentReports.License(status).Should().Be(
            new LicensedAgentLicenseDto(null, null, "None", null, true), "no licence loaded means not declared");
    }

    // ─── structural comparison ───────────────────────────────────────────────

    private static void AssertMatches(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected)
        {
            case null:
                actual.Should().BeNull($"{path} is null in the fixture");
                return;
            case JsonObject expectedObject:
            {
                actual.Should().BeOfType<JsonObject>($"{path} is an object in the fixture");
                var actualObject = actual!.AsObject();
                actualObject.Select(kv => kv.Key).Should().Equal(expectedObject.Select(kv => kv.Key),
                    $"{path} must carry exactly the fixture's fields, in its order");
                foreach (var (key, value) in expectedObject)
                    AssertMatches(value, actualObject[key], $"{path}.{key}");
                return;
            }
            case JsonArray expectedArray:
            {
                actual.Should().BeOfType<JsonArray>($"{path} is an array in the fixture");
                var actualArray = actual!.AsArray();
                actualArray.Should().HaveCount(expectedArray.Count, $"{path} length");
                for (var i = 0; i < expectedArray.Count; i++)
                    AssertMatches(expectedArray[i], actualArray[i], $"{path}[{i}]");
                return;
            }
            default:
            {
                actual.Should().NotBeNull($"{path} is not null in the fixture");
                var expectedValue = expected.AsValue();
                var actualValue = actual!.AsValue();
                actualValue.GetValueKind().Should().Be(expectedValue.GetValueKind(), $"{path} JSON type");
                if (expectedValue.GetValueKind() == JsonValueKind.String
                    && expectedValue.GetValue<string>() is var s && s.Length > 10 && s[10] == 'T')
                {
                    var rendered = actualValue.GetValue<string>();
                    rendered.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$", $"{path} is a canonical UTC instant");
                    Instant(actualValue).Should().Be(Instant(expectedValue), $"{path} instant");
                    return;
                }

                JsonNode.DeepEquals(actualValue, expectedValue).Should().BeTrue($"{path}: expected {expectedValue.ToJsonString()}, got {actualValue.ToJsonString()}");
                return;
            }
        }
    }
}
