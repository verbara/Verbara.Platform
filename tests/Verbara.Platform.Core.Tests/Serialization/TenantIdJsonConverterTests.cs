using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Serialization;

namespace Verbara.Platform.Core.Tests.Serialization;

[SuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Tests only")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Tests only")]
public class TenantIdJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new TenantIdJsonConverter() },
    };

    [Fact]
    public void Serialize_ShouldWritePlainString_WhenTenantId()
    {
        var id = new TenantId("tenant-001");
        var json = JsonSerializer.Serialize(id, Options);
        json.Should().Be("\"tenant-001\"");
    }

    [Fact]
    public void Deserialize_ShouldReadPlainString_WhenTenantId()
    {
        var id = JsonSerializer.Deserialize<TenantId>("\"tenant-001\"", Options);
        id.Value.Should().Be("tenant-001");
    }
}
