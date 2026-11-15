using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;

namespace Verbara.Platform.Api.Tests.QaAvailability;

/// <summary>
/// The availability body writes every nullable member as an explicit
/// <c>null</c>, even though both of the host's serializers omit nulls (<c>WhenWritingNull</c>). The options below are the
/// host's HTTP JSON options (<c>Program.cs</c>, <c>ConfigureHttpJsonOptions</c>); the second test uses the context's own.
/// </summary>
public sealed class QaAvailabilitySerializationTests
{
    private static readonly string[] AlwaysWritten =
        ["available", "analysisRunning", "reason", "transcriptSource", "speakerMode", "topicsConfigured", "complianceConfigured", "sentimentSupported", "backlog"];

    private static JsonSerializerOptions HostOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
        return options;
    }

    private static readonly QaAvailabilityDto AllNull =
        new(false, false, null, null, null, null, null, null, null);

    private static void ShouldWriteEveryMember(JsonObject body)
    {
        body.Select(p => p.Key).Should().BeEquivalentTo(AlwaysWritten);
        foreach (var name in AlwaysWritten.Skip(2))
        {
            body.ContainsKey(name).Should().BeTrue(name);
            body[name].Should().BeNull(name);
        }
    }

    [Fact]
    public void Serialize_ShouldWriteEachNullMemberAsExplicitNull_WhenUsingTheHostHttpOptions()
    {
        var json = JsonSerializer.Serialize(AllNull, HostOptions().GetTypeInfo(typeof(QaAvailabilityDto)));

        ShouldWriteEveryMember(JsonNode.Parse(json)!.AsObject());
    }

    [Fact]
    public void Serialize_ShouldWriteEachNullMemberAsExplicitNull_WhenUsingTheContextOptions()
    {
        var json = JsonSerializer.Serialize(AllNull, ApiJsonContext.Default.QaAvailabilityDto);

        ShouldWriteEveryMember(JsonNode.Parse(json)!.AsObject());
    }

    [Fact]
    public void Serialize_ShouldWriteTheBacklogInCamelCase_WhenTheBacklogIsPresent()
    {
        var since = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var body = new QaAvailabilityDto(
            true, true, null, QaAvailabilityDto.TranscriptSourceRecording, QaAvailabilityDto.SpeakerModeTwoTrack, true, false, true,
            new QaBacklogDto(0, 0, since));

        var node = JsonNode.Parse(JsonSerializer.Serialize(body, HostOptions().GetTypeInfo(typeof(QaAvailabilityDto))))!.AsObject();

        node["available"]!.GetValue<bool>().Should().BeTrue();
        node["reason"].Should().BeNull();
        node.ContainsKey("reason").Should().BeTrue();
        node["transcriptSource"]!.GetValue<string>().Should().Be("recording");
        node["speakerMode"]!.GetValue<string>().Should().Be("two_track");
        node["topicsConfigured"]!.GetValue<bool>().Should().BeTrue();
        node["complianceConfigured"]!.GetValue<bool>().Should().BeFalse();
        node["sentimentSupported"]!.GetValue<bool>().Should().BeTrue();
        var backlog = node["backlog"]!.AsObject();
        backlog["waiting"]!.GetValue<long>().Should().Be(0);
        backlog["notAnalysed"]!.GetValue<long>().Should().Be(0);
        backlog["since"]!.GetValue<DateTimeOffset>().Should().Be(since);
    }
}
