namespace Asterisk.Platform.Core.Tests;

public class EntityIdTests
{
    [Fact]
    public void New_ShouldCreateUniqueId()
    {
        var a = EntityId.New();
        var b = EntityId.New();
        a.Should().NotBe(b);
    }

    [Fact]
    public void From_ShouldCreateFromString()
    {
        var id = EntityId.From("abc-123");
        id.Value.Should().Be("abc-123");
    }

    [Fact]
    public void From_ShouldThrow_WhenEmpty()
    {
        var act = () => EntityId.From("");
        act.Should().Throw<ArgumentException>();
    }
}
