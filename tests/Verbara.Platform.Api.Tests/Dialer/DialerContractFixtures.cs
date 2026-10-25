using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-honours-dnc-callbacks-and-agent-dispositions: the change's cross-repo contract fixtures (impact.yaml
/// <c>fixtures:</c>), copied verbatim into <c>Dialer/Fixtures/</c>. Platform.Web pins the same files in its contract
/// tests, so a shape that drifts fails on either side.
/// </summary>
internal static class DialerContractFixtures
{
    public static JsonNode Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "Dialer", "Fixtures", name)))!;

    public static JsonObject Object(string name) => Load(name).AsObject();

    /// <summary>
    /// Asserts that <paramref name="actual"/> has exactly the fixture's member names and, member by member, the
    /// fixture's JSON type; nested objects are compared the same way. Values are not compared (a timestamp or an id
    /// differs by nature).
    /// </summary>
    public static void AssertSameMembers(JsonObject expected, JsonObject actual, string path = "$")
    {
        actual.Select(kv => kv.Key).Should().BeEquivalentTo(
            expected.Select(kv => kv.Key), $"{path} has exactly the fixture's members");
        foreach (var (name, want) in expected)
        {
            var got = actual[name];
            if (want is null)
            {
                continue;
            }

            got.Should().NotBeNull($"{path}.{name} is written");
            got!.GetValueKind().Should().Be(want.GetValueKind(), $"{path}.{name} keeps the fixture's JSON type");
            if (want is JsonObject wantObject)
            {
                AssertSameMembers(wantObject, got.AsObject(), $"{path}.{name}");
            }
        }
    }

    public static JsonObject Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, info))!.AsObject();
}
