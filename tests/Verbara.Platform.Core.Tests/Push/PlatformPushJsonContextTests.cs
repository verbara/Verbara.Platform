using System.Text.Json;

using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;

namespace Verbara.Platform.Core.Tests.Push;

public sealed class PlatformPushJsonContextTests
{
    [Fact]
    public void TypificationSubmittedEvent_ShouldRoundtrip_WhenSerializedWithContext()
    {
        var evt = new TypificationSubmittedEvent(
            TenantId: "acme",
            ConversationId: "conv-7",
            SchemaId: "schema-1",
            SchemaVersion: 3,
            LeafNodeId: "leaf-99",
            AgentId: "agent-5");

        var json = JsonSerializer.Serialize(evt, PlatformPushJsonContext.Default.TypificationSubmittedEvent);
        var deserialized = JsonSerializer.Deserialize(json, PlatformPushJsonContext.Default.TypificationSubmittedEvent);

        deserialized.Should().NotBeNull();
        deserialized!.TenantId.Should().Be("acme");
        deserialized.ConversationId.Should().Be("conv-7");
        deserialized.SchemaId.Should().Be("schema-1");
        deserialized.SchemaVersion.Should().Be(3);
        deserialized.LeafNodeId.Should().Be("leaf-99");
        deserialized.AgentId.Should().Be("agent-5");
        deserialized.EventType.Should().Be("typification.submitted");
    }
}
