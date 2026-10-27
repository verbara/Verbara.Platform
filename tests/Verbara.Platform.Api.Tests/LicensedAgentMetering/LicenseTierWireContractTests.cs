using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Queues.Licensing;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests.LicensedAgentMetering;

/// <summary>
/// hosted-tiers-retired-self-hosted-only (licensed-agent-reporting, "Licence tier values on the wire exclude the
/// retired hosted tiers"; design D3): the licence tier is written by name on
/// <c>GET /management/system/license/status</c> (Pro's <see cref="LicenseStatusSnapshot"/> through
/// <see cref="ApiJsonContext"/>) and in the <c>license</c> block of the peaks report and the export
/// (<see cref="LicensedAgentReports.License"/>), and is exactly one of the five defined tiers. The retired hosted
/// tiers (integers 4 and 5, named in the v2 fixture's <c>_retiredTierValues</c>), and any other undefined signed tier, are reported as
/// <c>None</c>: never their old names, never the number, never the number as a string
/// (verbara-meta/PDR-0006). Frozen by <c>license-status-snapshot.v2.json</c> and
/// <c>licensed-agent-license-block.v2.json</c>.
/// </summary>
public sealed class LicenseTierWireContractTests
{
    private static readonly string[] AllowedTiers = ["None", "Developer", "SelfHostStartup", "SelfHostBusiness", "WhiteLabel"];

    private static JsonObject Fixture(string name)
    {
        var path = Path.Join(AppContext.BaseDirectory, "LicensedAgentMetering", "Fixtures", name);
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static LicenseKey Key(LicenseTier tier, LicenseFeature features = LicenseFeature.All) => new(
        "lic-7f3c2a91", "Example Contact Ltd", tier, new DateTimeOffset(2027, 9, 1, 0, 0, 0, TimeSpan.Zero),
        features, 50, 3, "signature", ["sha256:aa", "sha256:bb", "sha256:cc", "sha256:dd"]);

    private static (LicenseStatusTracker Tracker, LicenseStatusReader Reader) Loaded(LicenseValidationResult result, LicenseKey key)
    {
        var tracker = new LicenseStatusTracker();
        tracker.Update(result, key);
        return (tracker, new LicenseStatusReader(tracker, Options.Create(new LicenseOptions())));
    }

    private static JsonObject StatusJson(LicenseStatusReader reader) =>
        JsonNode.Parse(JsonSerializer.Serialize(reader.Snapshot(), ApiJsonContext.Default.LicenseStatusSnapshot))!.AsObject();

    /// <summary>The <c>license</c> block exactly as the peaks report writes it (the export carries the same DTO).</summary>
    private static JsonObject LicenseBlockJson(ILicenseStatus status)
    {
        var peaks = LicensedAgentReports.BuildPeaks(
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31),
            new LicensedAgentPeaksData("UTC", [], new Dictionary<string, LicensedAgentTenantRef>(), []),
            LicensedAgentReports.License(status));
        var json = JsonNode.Parse(JsonSerializer.Serialize(peaks, ApiJsonContext.Default.LicensedAgentPeaksResponse))!;
        return json["license"]!.AsObject();
    }

    private static void TierShouldBeTheString(JsonObject body, string expected)
    {
        var tier = body["tier"];
        tier.Should().NotBeNull("`tier` is always written");
        tier!.GetValueKind().Should().Be(JsonValueKind.String, "`tier` is never a JSON number on the wire");
        tier.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public void LicenseTier_ShouldDefineExactlyTheFiveSelfHostedTiers()
    {
        // The OpenAPI LicenseTier schema and the generated Web types are derived from this member set.
        Enum.GetNames<LicenseTier>().Should().Equal(AllowedTiers);
        Fixture("license-status-snapshot.v2.json")["_allowedTierValues"]!.AsArray().Select(v => v!.GetValue<string>())
            .Should().Equal(AllowedTiers);
        Fixture("licensed-agent-license-block.v2.json")["_allowedTierValues"]!.AsArray().Select(v => v!.GetValue<string>())
            .Should().Equal(AllowedTiers);
    }

    [Theory]
    [InlineData(LicenseTier.None, "None")]
    [InlineData(LicenseTier.Developer, "Developer")]
    [InlineData(LicenseTier.SelfHostStartup, "SelfHostStartup")]
    [InlineData(LicenseTier.SelfHostBusiness, "SelfHostBusiness")]
    [InlineData(LicenseTier.WhiteLabel, "WhiteLabel")]
    public void Tier_ShouldBeWrittenByName_WhenTheLoadedTierIsDefined(LicenseTier tier, string expected)
    {
        var (tracker, reader) = Loaded(LicenseValidationResult.Valid, Key(tier));

        TierShouldBeTheString(StatusJson(reader), expected);
        TierShouldBeTheString(LicenseBlockJson(tracker), expected);
        LicensedAgentReports.License(tracker).Tier.Should().Be(expected);
    }

    [Theory]
    [InlineData(4, LicenseFeature.All)]
    [InlineData(4, LicenseFeature.None)]
    [InlineData(5, LicenseFeature.All)]
    [InlineData(5, LicenseFeature.None)]
    [InlineData(99, LicenseFeature.All)]
    [InlineData(99, LicenseFeature.None)]
    public void Tier_ShouldBeNone_WhenTheSignedTierIsRetiredOrUndefined(int signedTier, LicenseFeature features)
    {
        // A correctly signed licence whose Tier integer is 4, 5 or 99 fails validation (Pro, design D3) and the
        // tracker reports its tier as None, whatever its Features.
        var (tracker, reader) = Loaded(LicenseValidationResult.Invalid, Key((LicenseTier)signedTier, features));

        var status = StatusJson(reader);
        status["isValid"]!.GetValue<bool>().Should().BeFalse();
        TierShouldBeTheString(status, "None");

        var license = LicenseBlockJson(tracker);
        TierShouldBeTheString(license, "None");
        LicensedAgentReports.License(tracker).Tier.Should().Be("None");

        var number = signedTier.ToString(CultureInfo.InvariantCulture);
        var retiredNames = Fixture("license-status-snapshot.v2.json")["_retiredTierValues"]!.AsObject().Select(kv => kv.Key).ToList();
        retiredNames.Should().HaveCount(2);
        foreach (var body in new[] { status.ToJsonString(), license.ToJsonString() })
        {
            foreach (var retired in retiredNames)
                body.Should().NotContain(retired);
            body.Should().NotContain($"\"tier\":{number}").And.NotContain($"\"tier\":\"{number}\"");
        }
    }

    [Fact]
    public void Tier_ShouldBeNone_EvenWhenAnUndefinedTierIsRecordedAsValid()
    {
        // Defence in depth: the reported tier never leaves the enum, whatever the validation result recorded.
        var (tracker, reader) = Loaded(LicenseValidationResult.Valid, Key((LicenseTier)4));

        TierShouldBeTheString(StatusJson(reader), "None");
        TierShouldBeTheString(LicenseBlockJson(tracker), "None");
    }

    [Fact]
    public void LicenseStatus_ShouldMatchTheV2Fixture_FieldForField()
    {
        var fixture = Fixture("license-status-snapshot.v2.json");
        var snapshot = new LicenseStatusSnapshot
        {
            IsLoaded = fixture["isLoaded"]!.GetValue<bool>(),
            IsValid = fixture["isValid"]!.GetValue<bool>(),
            Tier = Enum.Parse<LicenseTier>(fixture["tier"]!.GetValue<string>()),
            ExpiresAt = DateTimeOffset.Parse(fixture["expiresAt"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            MaxAgents = fixture["maxAgents"]!.GetValue<int>(),
            MaxNodes = fixture["maxNodes"]!.GetValue<int>(),
            AuthorizedDigestsCount = fixture["authorizedDigestsCount"]!.GetValue<int>(),
            LastValidationResult = Enum.Parse<LicenseValidationResult>(fixture["lastValidationResult"]!.GetValue<string>()),
            LastValidationAt = DateTimeOffset.Parse(fixture["lastValidationAt"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            RevalidationInterval = TimeSpan.Parse(fixture["revalidationInterval"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            Licensee = fixture["licensee"]!.GetValue<string>(),
        };

        var json = JsonNode.Parse(JsonSerializer.Serialize(snapshot, ApiJsonContext.Default.LicenseStatusSnapshot))!.AsObject();

        var expectedFields = fixture.Select(kv => kv.Key).Where(k => !k.StartsWith('_')).ToList();
        json.Select(kv => kv.Key).Should().BeEquivalentTo(expectedFields, "the status carries exactly the fixture's fields");
        foreach (var field in expectedFields)
        {
            var expected = fixture[field]!.AsValue();
            var actual = json[field]!.AsValue();
            actual.GetValueKind().Should().Be(expected.GetValueKind(), $"{field} JSON type");
            if (field is "expiresAt" or "lastValidationAt")
            {
                DateTimeOffset.Parse(actual.GetValue<string>(), CultureInfo.InvariantCulture)
                    .Should().Be(DateTimeOffset.Parse(expected.GetValue<string>(), CultureInfo.InvariantCulture), field);
                continue;
            }

            JsonNode.DeepEquals(actual, expected).Should().BeTrue($"{field}: expected {expected.ToJsonString()}, got {actual.ToJsonString()}");
        }
    }

    [Fact]
    public void LicenseBlock_ShouldMatchTheV2Fixture_FieldForField()
    {
        var fixture = Fixture("licensed-agent-license-block.v2.json");
        var expected = fixture["license"]!.AsObject();
        var status = Substitute.For<ILicenseStatus>();
        status.LicenseId.Returns(expected["licenseId"]!.GetValue<string>());
        status.Licensee.Returns(expected["licensee"]!.GetValue<string>());
        status.Tier.Returns(Enum.Parse<LicenseTier>(expected["tier"]!.GetValue<string>()));
        status.MaxAgents.Returns(expected["maxAgents"]!.GetValue<int>());

        var actual = LicenseBlockJson(status);

        actual.Select(kv => kv.Key).Should().Equal(expected.Select(kv => kv.Key), "the block keeps its fixture fields, in order");
        JsonNode.DeepEquals(actual, expected).Should().BeTrue($"expected {expected.ToJsonString()}, got {actual.ToJsonString()}");
    }
}
