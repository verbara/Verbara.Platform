using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Tests;

public class TagTests
{
    [Fact]
    public void Constructor_ShouldCreateTag_WhenValidInput()
    {
        var tag = new Tag
        {
            TagId = EntityId.From("tag-001"),
            TenantId = new TenantId("t1"),
            Name = "VIP",
            Source = TagSource.Manual,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        tag.Name.Should().Be("VIP");
        tag.Source.Should().Be(TagSource.Manual);
    }
}
