using System.Text.Json;

namespace Verbara.Platform.Core.Tests.Serialization;

public class PlatformCoreJsonContextTests
{
    [Fact]
    public void ChannelAddress_ShouldRoundtrip_WhenSerializedWithContext()
    {
        var address = new ChannelAddress(ChannelType.WhatsApp, "+1234567890");

        var json = JsonSerializer.Serialize(address, PlatformCoreJsonContext.Default.ChannelAddress);
        var deserialized = JsonSerializer.Deserialize(json, PlatformCoreJsonContext.Default.ChannelAddress);

        deserialized.Should().Be(address);
    }

    [Fact]
    public void TenantId_ShouldRoundtrip_WhenSerializedWithContext()
    {
        var id = new TenantId("tenant-001");

        var json = JsonSerializer.Serialize(id, PlatformCoreJsonContext.Default.TenantId);
        var deserialized = JsonSerializer.Deserialize(json, PlatformCoreJsonContext.Default.TenantId);

        deserialized.Should().Be(id);
    }

    [Fact]
    public void PagedResultOfString_ShouldRoundtrip_WhenSerializedWithContext()
    {
        var result = new PagedResult<string>(["a", "b"], totalCount: 5, page: 1, pageSize: 2);

        var json = JsonSerializer.Serialize(result, PlatformCoreJsonContext.Default.PagedResultString);
        var deserialized = JsonSerializer.Deserialize(json, PlatformCoreJsonContext.Default.PagedResultString);

        deserialized!.TotalCount.Should().Be(5);
        deserialized.Items.Should().HaveCount(2);
    }
}
