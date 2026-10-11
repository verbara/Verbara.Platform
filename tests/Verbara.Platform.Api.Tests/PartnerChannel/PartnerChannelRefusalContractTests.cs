using System.Text.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;

namespace Verbara.Platform.Api.Tests.PartnerChannel;

/// <summary>
/// partner-channel-pilot-fixes 1.6 (design D4): each typed refusal, serialised through <see cref="ApiJsonContext"/>
/// with the fixture's own values, is the frozen fixture body field for field — the same names in the same order,
/// the same JSON types and the same values, nothing more.
/// </summary>
public sealed class PartnerChannelRefusalContractTests
{
    private static JsonObject Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, info))!.AsObject();

    private static void AssertSame(JsonObject expected, JsonObject actual)
    {
        actual.Select(kv => kv.Key).Should().Equal(expected.Select(kv => kv.Key), "exactly the fixture's fields, in its order");
        JsonNode.DeepEquals(actual, expected).Should().BeTrue($"expected {expected.ToJsonString()}, got {actual.ToJsonString()}");
    }

    private static string Text(JsonObject body, string name) => body[name]!.GetValue<string>();

    [Fact]
    public void CurrencyInvalidRefusal_ShouldMatchTheFrozenFixture_FieldForField()
    {
        var fixture = PartnerChannelTestKit.FixtureBody("rate-card-currency-refusals.json", "invalidCurrency");

        var json = Serialize(CurrencyInvalidRefusal.For(Text(fixture, "currency")), ApiJsonContext.Default.CurrencyInvalidRefusal);

        AssertSame(fixture, json);
        CurrencyInvalidRefusal.Code.Should().Be(Text(fixture, "error"));
    }

    [Fact]
    public void CurrencyInvalidRefusal_ShouldWriteCurrencyAsNull_WhenTheValueIsMissing()
    {
        var json = Serialize(CurrencyInvalidRefusal.For(null), ApiJsonContext.Default.CurrencyInvalidRefusal);

        json.ContainsKey("currency").Should().BeTrue("a nullable field is always written, never omitted");
        json["currency"].Should().BeNull();
    }

    [Fact]
    public void CurrencyMismatchRefusal_ShouldMatchTheFrozenFixture_FieldForField()
    {
        var fixture = PartnerChannelTestKit.FixtureBody("rate-card-currency-refusals.json", "currencyMismatch");

        var json = Serialize(
            CurrencyMismatchRefusal.For(Text(fixture, "partnerCurrency"), Text(fixture, "hostCurrency")),
            ApiJsonContext.Default.CurrencyMismatchRefusal);

        AssertSame(fixture, json);
        CurrencyMismatchRefusal.Code.Should().Be(Text(fixture, "error"));
    }

    [Fact]
    public void PlanAboveParentRefusal_ShouldMatchTheFrozenFixture_FieldForField()
    {
        var fixture = PartnerChannelTestKit.FixtureBody("plan-above-parent-409.json");

        var json = Serialize(
            PlanAboveParentRefusal.For(Text(fixture, "requestedPlan"), Text(fixture, "parentPlan"), Text(fixture, "parentTenantId")),
            ApiJsonContext.Default.PlanAboveParentRefusal);

        AssertSame(fixture, json);
        PlanAboveParentRefusal.Code.Should().Be(Text(fixture, "error"));
    }

    [Fact]
    public void DunningPartnerCustomerRefusal_ShouldMatchTheFrozenFixture_FieldForField()
    {
        var fixture = PartnerChannelTestKit.FixtureBody("dunning-partner-customer-409.json");

        var json = Serialize(
            DunningPartnerCustomerRefusal.For(Text(fixture, "tenantId"), Text(fixture, "partnerTenantId")),
            ApiJsonContext.Default.DunningPartnerCustomerRefusal);

        AssertSame(fixture, json);
        DunningPartnerCustomerRefusal.Code.Should().Be(Text(fixture, "error"));
    }
}
