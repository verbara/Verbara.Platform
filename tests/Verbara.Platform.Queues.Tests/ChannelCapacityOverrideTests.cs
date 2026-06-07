using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Queues.Tests;

public class ChannelCapacityOverrideTests
{
    // Local source-gen context (AOT path) — the production PostgresJsonContext that
    // PostgresAgentStore uses is internal to Verbara.Platform.Storage.Postgres and not
    // visible here, so we mirror its registration to exercise the same reflection-free
    // (de)serialization the storage layer performs against the agents.capacity jsonb column.
    [Fact]
    public void ChannelCapacityOverride_ShouldDeserializeEmptyJsonToAllNull_WhenLegacyRow()
    {
        // Legacy rows persist '{}'; every field must come back null = "inherit the tenant default".
        var result = JsonSerializer.Deserialize(
            "{}", CapacityTestJson.Ctx.ChannelCapacityOverride);

        result.Should().NotBeNull();
        result!.MaxVoice.Should().BeNull();
        result.MaxChat.Should().BeNull();
        result.MaxEmail.Should().BeNull();
        result.MaxSms.Should().BeNull();
        result.MaxTotal.Should().BeNull();
    }

    [Fact]
    public void ChannelCapacityOverride_ShouldRoundTripPerFieldNulls_WhenPartialOverride()
    {
        var original = new ChannelCapacityOverride { MaxChat = 7 };

        var json = JsonSerializer.Serialize(original, CapacityTestJson.Ctx.ChannelCapacityOverride);
        var result = JsonSerializer.Deserialize(json, CapacityTestJson.Ctx.ChannelCapacityOverride);

        result.Should().NotBeNull();
        result!.MaxChat.Should().Be(7);
        result.MaxVoice.Should().BeNull();
        result.MaxEmail.Should().BeNull();
        result.MaxSms.Should().BeNull();
        result.MaxTotal.Should().BeNull();
    }

    [Fact]
    public void ToEffective_ShouldInheritDefault_WhenFieldNull()
    {
        var defaults = new ChannelCapacity { MaxVoice = 2, MaxChat = 4, MaxEmail = 6, MaxSms = 8, MaxTotal = 9 };
        var @override = new ChannelCapacityOverride { MaxChat = 1 };

        var effective = @override.ToEffective(defaults);

        // Only MaxChat is overridden; the rest inherit the tenant default.
        effective.MaxChat.Should().Be(1);
        effective.MaxVoice.Should().Be(2);
        effective.MaxEmail.Should().Be(6);
        effective.MaxSms.Should().Be(8);
        effective.MaxTotal.Should().Be(9);
    }

    [Fact]
    public void ToEffective_ShouldUseOverride_WhenFieldSet()
    {
        var defaults = new ChannelCapacity { MaxVoice = 2, MaxChat = 4, MaxEmail = 6, MaxSms = 8, MaxTotal = 9 };
        var @override = new ChannelCapacityOverride
        {
            MaxVoice = 0,
            MaxChat = 10,
            MaxEmail = 11,
            MaxSms = 12,
            MaxTotal = 13,
        };

        var effective = @override.ToEffective(defaults);

        effective.MaxVoice.Should().Be(0);
        effective.MaxChat.Should().Be(10);
        effective.MaxEmail.Should().Be(11);
        effective.MaxSms.Should().Be(12);
        effective.MaxTotal.Should().Be(13);
    }
}

[JsonSerializable(typeof(ChannelCapacityOverride))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CapacityTestJsonContext : JsonSerializerContext;

internal static class CapacityTestJson
{
    internal static readonly CapacityTestJsonContext Ctx = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });
}
