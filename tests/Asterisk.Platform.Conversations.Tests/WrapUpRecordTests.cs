using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations.Tests;

public class WrapUpRecordTests
{
    [Fact]
    public void Constructor_ShouldCreateRecord_WhenValidInput()
    {
        var record = new WrapUpRecord
        {
            TenantId = new TenantId("tenant-001"),
            ConversationId = EntityId.From("conv-001"),
            AgentId = EntityId.From("agent-001"),
            DispositionId = EntityId.From("disp-001"),
            Notes = "Customer needs callback",
            Duration = TimeSpan.FromSeconds(45),
            CompletedAt = DateTimeOffset.UtcNow,
        };

        record.Notes.Should().Be("Customer needs callback");
        record.Duration.TotalSeconds.Should().Be(45);
    }
}
