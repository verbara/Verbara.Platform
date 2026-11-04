using Verbara.Platform.Core;

namespace Verbara.Platform.Core.Tests;

/// <summary>Design D2: the level-1 key is built only from a random version-4 GUID.</summary>
public class WebChatVisitorKeyTests
{
    private const string FixtureKey = "webchat-visitor:rcfqwP4uUDSA6hgRqgUIE3hCFFsXvDuxhE3MgUu0Y1M";

    [Theory]
    [InlineData("3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b")]
    [InlineData("3F2B8C1E-9A4D-4E6F-8B7A-1C2D3E4F5A6B")]
    [InlineData("3f2b8c1e9a4d4e6f8b7a1c2d3e4f5a6b")]
    [InlineData("3F2B8C1E9A4D4E6F8B7A1C2D3E4F5A6B")]
    public void TryCreate_ShouldMapToFixtureKey_WhenVersion4GuidInAnyForm(string visitorId)
    {
        WebChatVisitorKey.TryCreate(visitorId, out var key).Should().BeTrue();

        key.Should().Be(new ChannelAddress(ChannelType.WebChat, FixtureKey));
    }

    [Theory]
    [InlineData("3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b")] // variant 8
    [InlineData("3f2b8c1e-9a4d-4e6f-9b7a-1c2d3e4f5a6b")] // variant 9
    [InlineData("3f2b8c1e-9a4d-4e6f-ab7a-1c2d3e4f5a6b")] // variant a
    [InlineData("3f2b8c1e-9a4d-4e6f-bb7a-1c2d3e4f5a6b")] // variant b
    public void TryCreate_ShouldAccept_WhenVariantIsRfc4122(string visitorId) =>
        WebChatVisitorKey.TryCreate(visitorId, out _).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")] // nil
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")] // max
    [InlineData("3f2b8c1e-9a4d-1e6f-8b7a-1c2d3e4f5a6b")] // version 1
    [InlineData("3f2b8c1e-9a4d-7e6f-8b7a-1c2d3e4f5a6b")] // version 7
    [InlineData("3f2b8c1e-9a4d-4e6f-7b7a-1c2d3e4f5a6b")] // variant 7
    [InlineData("3f2b8c1e-9a4d-4e6f-cb7a-1c2d3e4f5a6b")] // variant c
    [InlineData(" 3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b")]
    [InlineData("3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b ")]
    [InlineData("{3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b}")]
    [InlineData("3f2b8c1e-9a4d4e6f-8b7a-1c2d3e4f5a6b0")] // 36 long, a hyphen out of place
    [InlineData("3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6g")] // not hex
    [InlineData("+f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b")] // a sign is not hex
    [InlineData("3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6")] // short
    public void TryCreate_ShouldRefuse_WhenNotAVersion4Guid(string? visitorId)
    {
        WebChatVisitorKey.TryCreate(visitorId, out var key).Should().BeFalse();

        key.Should().BeNull();
    }
}
