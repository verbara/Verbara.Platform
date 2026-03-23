using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Serialization;

namespace Asterisk.Platform.Core.Tests.Serialization;

[SuppressMessage("Trimming", "IL2026")]
[SuppressMessage("AOT", "IL3050")]
public class EntityIdJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new EntityIdJsonConverter() },
    };

    [Fact]
    public void Serialize_ShouldWritePlainString_WhenEntityId()
    {
        var id = EntityId.From("abc123");
        var json = JsonSerializer.Serialize(id, Options);
        json.Should().Be("\"abc123\"");
    }

    [Fact]
    public void Deserialize_ShouldReadPlainString_WhenEntityId()
    {
        var id = JsonSerializer.Deserialize<EntityId>("\"abc123\"", Options);
        id.Value.Should().Be("abc123");
    }

    [Fact]
    public void RoundTrip_ShouldPreserveValue_WhenNestedInObject()
    {
        var obj = new TestRecord(EntityId.From("x1"), "hello");
        var json = JsonSerializer.Serialize(obj, Options);
        json.Should().Contain("\"x1\"");
        json.Should().NotContain("\"value\"");

        var back = JsonSerializer.Deserialize<TestRecord>(json, Options);
        back!.Id.Value.Should().Be("x1");
    }

    private sealed record TestRecord(EntityId Id, string Name);
}
