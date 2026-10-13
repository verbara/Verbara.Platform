using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen 1.1 (design D7, D12): each wire record, serialised through <see cref="ApiJsonContext"/>
/// with the fixture's own values, is the frozen fixture body field for field — the same names in the same order,
/// the same JSON types and values — and every nullable member is written as null, never omitted.
/// </summary>
public sealed class SupervisorListenContractTests
{
    private static JsonObject Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, info))!.AsObject();

    private static void AssertSame(JsonObject expected, JsonObject actual) => SupervisorListenTestKit.AssertSame(expected, actual);

    private static string? Text(JsonObject body, string name) => body[name]?.GetValue<string>();

    private static DateTimeOffset Time(JsonObject body, string name) =>
        DateTimeOffset.Parse(Text(body, name)!, CultureInfo.InvariantCulture);

    [Fact]
    public void SupervisorListenRefusal_ShouldMatchEveryFrozenRefusal_FieldForField()
    {
        var entries = SupervisorListenTestKit.Refusals().ToList();
        entries.Should().HaveCount(12, "the fixture freezes twelve refusal codes");

        foreach (var (key, entry) in entries)
        {
            var body = entry["body"]!.AsObject();
            var code = Text(body, "error")!;

            var json = Serialize(
                SupervisorListenRefusal.For(code, Text(body, "conversationId"), Text(body, "listenId")),
                ApiJsonContext.Default.SupervisorListenRefusal);

            AssertSame(body, json);
            SupervisorListenRefusal.StatusFor(code).Should().Be(entry["status"]!.GetValue<int>(), $"{key} keeps its frozen status");
        }
    }

    [Fact]
    public void SupervisorListenRefusal_ShouldExposeEveryFrozenCodeAsAConstant()
    {
        var codes = SupervisorListenTestKit.Refusals()
            .Select(r => Text(r.Entry["body"]!.AsObject(), "error"))
            .ToList();

        codes.Should().BeEquivalentTo(
        [
            SupervisorListenRefusal.NotPermitted,
            SupervisorListenRefusal.InvalidDestination,
            SupervisorListenRefusal.VoiceUnavailable,
            SupervisorListenRefusal.NotLeader,
            SupervisorListenRefusal.ConversationNotFound,
            SupervisorListenRefusal.NoVoiceCall,
            SupervisorListenRefusal.AlreadyListening,
            SupervisorListenRefusal.NoSupervisorEndpoint,
            SupervisorListenRefusal.NoListenPhone,
            SupervisorListenRefusal.NoTrunk,
            SupervisorListenRefusal.OriginateFailed,
            SupervisorListenRefusal.ListenNotFound,
        ]);
    }

    [Fact]
    public void SupervisorListenRefusal_ShouldWriteNullMembers_WhenTheyAreMissing()
    {
        var json = Serialize(SupervisorListenRefusal.For(SupervisorListenRefusal.ListenNotFound, null, null),
            ApiJsonContext.Default.SupervisorListenRefusal);

        json.ContainsKey("conversationId").Should().BeTrue("a nullable field is always written, never omitted");
        json.ContainsKey("listenId").Should().BeTrue("a nullable field is always written, never omitted");
        json["conversationId"].Should().BeNull();
        json["listenId"].Should().BeNull();
    }

    [Theory]
    [InlineData("start")]
    [InlineData("current")]
    public void SupervisorListenDto_ShouldMatchTheFrozenFixture_FieldForField(string key)
    {
        var body = SupervisorListenTestKit.FixtureBody("supervisor-listen-start.v1.json", key);
        DateTimeOffset? listeningSince = body["listeningSince"] is null ? null : Time(body, "listeningSince");

        var json = Serialize(
            new SupervisorListenDto(
                Text(body, "listenId")!, Text(body, "conversationId")!, Text(body, "destination")!,
                Text(body, "state")!, Time(body, "startedAt"), listeningSince),
            ApiJsonContext.Default.SupervisorListenDto);

        AssertSame(body, json);
    }

    [Fact]
    public void SupervisorListenStoppedDto_ShouldMatchTheFrozenFixture_FieldForField()
    {
        var body = SupervisorListenTestKit.FixtureBody("supervisor-listen-stop.v1.json");

        var json = Serialize(
            new SupervisorListenStoppedDto(
                Text(body, "listenId")!, Text(body, "conversationId")!, Time(body, "stoppedAt"), Text(body, "endReason")!),
            ApiJsonContext.Default.SupervisorListenStoppedDto);

        AssertSame(body, json);
    }

    [Fact]
    public void SupervisorListenEndpointDto_ShouldMatchTheFrozenFixture_FieldForField()
    {
        var body = SupervisorListenTestKit.FixtureBody("supervisor-sip-credentials.v1.json");

        var json = Serialize(
            new SupervisorListenEndpointDto(
                Text(body, "endpointId")!, Text(body, "authorizationUsername")!, Text(body, "sipPassword")!,
                Text(body, "displayName")!, Text(body, "listenCallerName")!),
            ApiJsonContext.Default.SupervisorListenEndpointDto);

        AssertSame(body, json);
        SupervisorListenEndpointDto.CallerName.Should().Be(Text(body, "listenCallerName"));
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    public void ListenPhoneDto_ShouldMatchTheFrozenFixture_FieldForField(string key)
    {
        var body = SupervisorListenTestKit.FixtureBody("supervisor-listen-phone.v1.json", key);

        var json = Serialize(new ListenPhoneDto(Text(body, "userId")!, Text(body, "phoneNumber")),
            ApiJsonContext.Default.ListenPhoneDto);

        AssertSame(body, json);
    }

    [Fact]
    public void ListenPhoneDto_ShouldWritePhoneNumberAsNull_WhenItIsCleared()
    {
        var json = Serialize(new ListenPhoneDto("u1", null), ApiJsonContext.Default.ListenPhoneDto);

        json.ContainsKey("phoneNumber").Should().BeTrue();
        json["phoneNumber"].Should().BeNull();
    }

    [Fact]
    public void ListenPhoneRequest_ShouldReadTheFrozenRequest()
    {
        var request = SupervisorListenTestKit.Fixture("supervisor-listen-phone.v1.json")["set"]!["request"]!.ToJsonString();

        var parsed = JsonSerializer.Deserialize(request, ApiJsonContext.Default.ListenPhoneRequest);

        parsed!.PhoneNumber.Should().Be("+573001234567");
    }

    [Fact]
    public void ListenPhoneRefusal_ShouldMatchTheFrozenFixture_FieldForField()
    {
        var body = SupervisorListenTestKit.FixtureBody("supervisor-listen-phone.v1.json", "invalid");

        var json = Serialize(ListenPhoneRefusal.For(Text(body, "phoneNumber")), ApiJsonContext.Default.ListenPhoneRefusal);

        AssertSame(body, json);
        ListenPhoneRefusal.Code.Should().Be(Text(body, "error"));
        SupervisorListenTestKit.FixtureStatus("supervisor-listen-phone.v1.json", "invalid").Should().Be(400);
    }

    [Fact]
    public void ListenPhoneRefusal_ShouldWritePhoneNumberAsNull_WhenTheValueIsMissing()
    {
        var json = Serialize(ListenPhoneRefusal.For(null), ApiJsonContext.Default.ListenPhoneRefusal);

        json.ContainsKey("phoneNumber").Should().BeTrue();
        json["phoneNumber"].Should().BeNull();
    }

    [Fact]
    public void SupervisorListenStartRequest_ShouldReadTheDestination_AndIgnoreAPhoneNumber()
    {
        var parsed = JsonSerializer.Deserialize(
            """{"destination":"phone","phoneNumber":"+15550001111"}""", ApiJsonContext.Default.SupervisorListenStartRequest);

        parsed!.Destination.Should().Be("phone");
        typeof(SupervisorListenStartRequest).GetProperties().Select(p => p.Name).Should().Equal(["Destination"],
            "the request carries a destination kind and never a number");
    }
}
