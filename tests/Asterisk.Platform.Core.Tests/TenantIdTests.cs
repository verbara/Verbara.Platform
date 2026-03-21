namespace Asterisk.Platform.Core.Tests;

public class TenantIdTests
{
    [Fact]
    public void Constructor_ShouldCreateTenantId_WhenValidValue()
    {
        var id = new TenantId("tenant-001");
        id.Value.Should().Be("tenant-001");
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenEmpty()
    {
        var act = () => new TenantId("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Equality_ShouldBeTrue_WhenSameValue()
    {
        var a = new TenantId("t1");
        var b = new TenantId("t1");
        a.Should().Be(b);
    }

    [Fact]
    public void ImplicitConversion_ShouldReturnValue_WhenCastToString()
    {
        var id = new TenantId("t1");
        string value = id;
        value.Should().Be("t1");
    }
}
