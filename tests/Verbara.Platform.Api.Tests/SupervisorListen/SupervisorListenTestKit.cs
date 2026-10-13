using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>supervisor-silent-listen: reads the change's frozen fixtures copied into this test project.</summary>
internal static class SupervisorListenTestKit
{
    /// <summary>The whole fixture file.</summary>
    public static JsonObject Fixture(string file)
    {
        var path = Path.Join(AppContext.BaseDirectory, "SupervisorListen", "Fixtures", file);
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    /// <summary>The <c>body</c> of a fixture; <paramref name="key"/> picks one entry of a file that holds several.</summary>
    public static JsonObject FixtureBody(string file, string? key = null)
    {
        var root = Fixture(file);
        var entry = key is null ? root : root[key]!.AsObject();
        return entry["body"]!.AsObject();
    }

    /// <summary>
    /// Asserts <paramref name="actual"/> has exactly the fixture's fields, in its order, with the same JSON kinds and
    /// values. A timestamp is compared as an instant: System.Text.Json trims trailing zeros of the fraction
    /// (<c>09.870</c> is written <c>09.87</c>), which is the same ISO-8601 instant.
    /// </summary>
    public static void AssertSame(JsonObject expected, JsonObject actual)
    {
        actual.Select(kv => kv.Key).Should().Equal(expected.Select(kv => kv.Key), "exactly the fixture's fields, in its order");
        foreach (var (name, want) in expected)
        {
            var got = actual[name];
            if (want is null)
            {
                got.Should().BeNull($"{name} is written as null");
                continue;
            }

            got.Should().NotBeNull($"{name} is written");
            got!.GetValueKind().Should().Be(want.GetValueKind(), $"{name} keeps the fixture's JSON type");
            if (want.GetValueKind() == System.Text.Json.JsonValueKind.String
                && IsInstant(want.GetValue<string>(), out var wantInstant)
                && IsInstant(got.GetValue<string>(), out var gotInstant))
            {
                gotInstant.Should().Be(wantInstant, $"{name} is the fixture's instant");
                continue;
            }

            JsonNode.DeepEquals(got, want).Should().BeTrue($"{name}: expected {want.ToJsonString()}, got {got.ToJsonString()}");
        }
    }

    private static bool IsInstant(string value, out DateTimeOffset instant)
    {
        instant = default;
        return value.Length >= 20 && value[4] == '-' && value[10] == 'T'
            && DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out instant);
    }

    /// <summary>The HTTP status a fixture entry declares.</summary>
    public static int FixtureStatus(string file, string key) => Fixture(file)[key]!["status"]!.GetValue<int>();

    /// <summary>Every refusal entry of <c>supervisor-listen-refusals.json</c> (the keys that are not comments).</summary>
    public static IEnumerable<(string Key, JsonObject Entry)> Refusals() =>
        Fixture("supervisor-listen-refusals.json")
            .Where(kv => !kv.Key.StartsWith('_'))
            .Select(kv => (kv.Key, kv.Value!.AsObject()));
}
