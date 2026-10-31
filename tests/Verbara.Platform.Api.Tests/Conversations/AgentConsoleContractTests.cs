using System.Text.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Tests.SupervisorListen;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// agent-console-contract-debt (C1, C5, C6, C7): the wire shapes the console reads, serialised through
/// <see cref="ApiJsonContext"/> with each fixture's own values, are the frozen bodies field for field. Platform.Web
/// generates its types from these shapes, so a renamed or added member goes red here first.
/// </summary>
public sealed class AgentConsoleContractTests
{
    private static JsonObject Fixture(string name)
    {
        var file = Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", name);
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    private static string Text(JsonNode? node, string name) => node![name]!.GetValue<string>();

    private static int Number(JsonObject body, string name) => body[name]!.GetValue<int>();

    private static string?[] Names(JsonNode? array) =>
        array!.AsArray().Select(value => value!.GetValue<string>()).ToArray<string?>();

    [Theory]
    [InlineData("toQueueWithNote")]
    [InlineData("toAgentWithNote")]
    [InlineData("toQueueWithoutNote")]
    [InlineData("_omittedNoteIsAlsoValid")]
    public void TransferRequest_ShouldBindEveryFixtureMember_WhenDeserialized(string key)
    {
        var fixture = Fixture("transfer-request.v1.json")["requests"]![key]!.AsObject();

        var request = JsonSerializer.Deserialize(fixture.ToJsonString(), ApiJsonContext.Default.TransferRequest)!;

        request.TargetQueueId.Should().Be(fixture["targetQueueId"]?.GetValue<string>());
        request.TargetAgentId.Should().Be(fixture["targetAgentId"]?.GetValue<string>());
        request.Note.Should().Be(fixture["note"]?.GetValue<string>());
    }

    [Fact]
    public void TransferRequest_ShouldLeaveNoteNull_WhenTheBodyOmitsIt()
    {
        var request = JsonSerializer.Deserialize("""{"targetAgentId":"a1"}""", ApiJsonContext.Default.TransferRequest)!;

        request.Should().Be(new TransferRequest(null, "a1"));
    }

    [Fact]
    public void TransferRequest_ShouldWriteTheFixtureMembers_WhenSerialized()
    {
        var fixture = Fixture("transfer-request.v1.json")["requests"]!["toQueueWithNote"]!;

        var json = JsonSerializer.Serialize(
            new TransferRequest(Text(fixture, "targetQueueId"), null, Text(fixture, "note")),
            ApiJsonContext.Default.TransferRequest);

        // The fixture's literal null is a request a client may send; this serializer omits it (host design H11).
        var expected = new JsonObject
        {
            ["targetQueueId"] = Text(fixture, "targetQueueId"),
            ["note"] = Text(fixture, "note"),
        };
        SupervisorListenTestKit.AssertSame(expected, JsonNode.Parse(json)!.AsObject());
    }

    [Theory]
    [InlineData("body")]
    [InlineData("emptyBody")]
    public void PagedResultOfTransferTargetQueueDto_ShouldMatchTheFixture_FieldForField(string key)
    {
        var body = Fixture("transfer-target-queues.v1.json")[key]!.AsObject();
        var items = body["items"]!.AsArray()
            .Select(item => new TransferTargetQueueDto(Text(item, "id"), Text(item, "name")))
            .ToList();

        var json = JsonSerializer.Serialize(
            new PagedResult<TransferTargetQueueDto>(items, Number(body, "totalCount"), Number(body, "page"), Number(body, "pageSize")),
            ApiJsonContext.Default.PagedResultTransferTargetQueueDto);

        SupervisorListenTestKit.AssertSame(body, JsonNode.Parse(json)!.AsObject());
    }

    [Theory]
    [InlineData("body")]
    public void PagedResultOfTransferTargetAgentDto_ShouldMatchTheFixture_FieldForField(string key)
    {
        var body = Fixture("transfer-target-agents.v1.json")[key]!.AsObject();
        var items = body["items"]!.AsArray()
            .Select(item => new TransferTargetAgentDto(
                Text(item, "id"),
                Text(item, "displayName"),
                Enum.Parse<AgentState>(Text(item, "state")),
                item!["canAcceptWork"]!.GetValue<bool>()))
            .ToList();

        var json = JsonSerializer.Serialize(
            new PagedResult<TransferTargetAgentDto>(items, Number(body, "totalCount"), Number(body, "page"), Number(body, "pageSize")),
            ApiJsonContext.Default.PagedResultTransferTargetAgentDto);

        SupervisorListenTestKit.AssertSame(body, JsonNode.Parse(json)!.AsObject());
    }

    [Fact]
    public void TransferTargetAgentDto_ShouldWriteEveryFixtureStateAsItsName_WhenSerialized()
    {
        var values = Names(Fixture("transfer-target-agents.v1.json")["stateValues"]);

        values.Should().BeEquivalentTo(Enum.GetNames<AgentState>());
        foreach (var value in values)
        {
            var json = JsonSerializer.Serialize(
                new TransferTargetAgentDto("a1", "Ana", Enum.Parse<AgentState>(value!), CanAcceptWork: false),
                ApiJsonContext.Default.TransferTargetAgentDto);

            Text(JsonNode.Parse(json), "state").Should().Be(value);
        }
    }

    [Theory]
    [InlineData("body")]
    [InlineData("emptyBody")]
    public void AvailableChannelDtoArray_ShouldMatchTheFixture_FieldForField(string key)
    {
        var body = Fixture("available-channels.v1.json")[key]!.AsArray();
        var items = body
            .Select(item => new AvailableChannelDto(Enum.Parse<ChannelType>(Text(item, "channel"))))
            .ToArray();

        var json = JsonSerializer.Serialize(items, ApiJsonContext.Default.AvailableChannelDtoArray);

        var written = JsonNode.Parse(json)!.AsArray();
        written.Should().HaveCount(body.Count);
        for (var i = 0; i < body.Count; i++)
            SupervisorListenTestKit.AssertSame(body[i]!.AsObject(), written[i]!.AsObject());
    }

    [Fact]
    public void AvailableChannelDto_ShouldWriteEveryFixtureChannelAsItsName_WhenSerialized()
    {
        var values = Names(Fixture("available-channels.v1.json")["channelValues"]);

        values.Should().BeEquivalentTo(Enum.GetNames<ChannelType>());
        foreach (var value in values)
        {
            var json = JsonSerializer.Serialize(
                new AvailableChannelDto(Enum.Parse<ChannelType>(value!)),
                ApiJsonContext.Default.AvailableChannelDto);

            Text(JsonNode.Parse(json), "channel").Should().Be(value);
        }
    }
}
