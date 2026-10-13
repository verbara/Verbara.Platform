using System.Text.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Tests.SupervisorListen;

namespace Verbara.Platform.Api.Tests.OutboundTrunk;

/// <summary>
/// outbound-calls-reach-the-trunk (design D4): <see cref="VoiceDialResponse"/> and <see cref="VoiceTransferResponse"/>,
/// serialised through <see cref="ApiJsonContext"/> with the fixture's own values, are the frozen bodies of
/// <c>voice-dial-transfer.v1.json</c> field for field: <c>trunkId</c> is always written, while <c>correlationId</c> and
/// <c>error</c> keep being omitted when null.
/// </summary>
public sealed class VoiceDialTransferContractTests
{
    internal static JsonObject FixtureEntry(string key)
    {
        var path = Path.Join(AppContext.BaseDirectory, "OutboundTrunk", "Fixtures", "voice-dial-transfer.v1.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject()[key]!.AsObject();
    }

    internal static JsonObject FixtureBody(string key) => FixtureEntry(key)["body"]!.AsObject();

    private static JsonObject Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, info))!.AsObject();

    private static string? Text(JsonObject body, string name) => body[name]?.GetValue<string>();

    private static long? Long(JsonObject body, string name) => body[name]?.GetValue<long>();

    [Theory]
    [InlineData("dialAccepted")]
    [InlineData("dialTrunkNotDialable")]
    public void VoiceDialResponse_ShouldMatchTheFrozenFixture_FieldForField(string key)
    {
        var body = FixtureBody(key);

        var json = Serialize(
            new VoiceDialResponse(body["accepted"]!.GetValue<bool>(), Text(body, "correlationId"), Text(body, "error"), Long(body, "trunkId")),
            ApiJsonContext.Default.VoiceDialResponse);

        SupervisorListenTestKit.AssertSame(body, json);
    }

    [Theory]
    [InlineData("transferAccepted")]
    [InlineData("transferTrunkNotDialable")]
    public void VoiceTransferResponse_ShouldMatchTheFrozenFixture_FieldForField(string key)
    {
        var body = FixtureBody(key);

        var json = Serialize(
            new VoiceTransferResponse(body["accepted"]!.GetValue<bool>(), Text(body, "error"), Long(body, "trunkId")),
            ApiJsonContext.Default.VoiceTransferResponse);

        SupervisorListenTestKit.AssertSame(body, json);
    }

    [Fact]
    public void VoiceDialResponse_ShouldWriteTrunkIdAsNull_AndOmitTheOtherNullMembers()
    {
        var json = JsonSerializer.Serialize(new VoiceDialResponse(false, null, "no-trunk"), ApiJsonContext.Default.VoiceDialResponse);

        json.Should().Be("""{"accepted":false,"error":"no-trunk","trunkId":null}""");
    }

    [Fact]
    public void VoiceTransferResponse_ShouldWriteTrunkIdAsNull_WhenAccepted()
    {
        var json = JsonSerializer.Serialize(new VoiceTransferResponse(true, null), ApiJsonContext.Default.VoiceTransferResponse);

        json.Should().Be("""{"accepted":true,"trunkId":null}""");
    }
}
