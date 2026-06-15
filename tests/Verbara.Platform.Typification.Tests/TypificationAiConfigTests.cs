using System.Text.Json;
using System.Text.Json.Serialization;
using Verbara.Platform.Typification;

namespace Verbara.Platform.Typification.Tests;

/// <summary>
/// P2b — unit tests for <see cref="TypificationAiConfig"/> defaults, band ordering
/// invariant, and JSON round-trip via a source-gen context.
/// </summary>
public sealed class TypificationAiConfigTests
{
    // ─── Defaults / band ordering ─────────────────────────────────────────────

    [Fact]
    public void AiConfig_ShouldDefaultToOff_WithOrderedBands()
    {
        var config = new TypificationAiConfig { EntityFieldMap = new Dictionary<string, string>() };

        config.Mode.Should().Be(AiMode.Off);
        config.Autonomous.Should().BeFalse();
        config.SuggestThreshold.Should().BeLessOrEqualTo(config.AutoApplyThreshold);
        config.AutoApplyThreshold.Should().BeLessOrEqualTo(config.AutonomousThreshold);
    }

    [Fact]
    public void AiConfig_ShouldHaveExpectedDefaults()
    {
        var config = new TypificationAiConfig { EntityFieldMap = new Dictionary<string, string>() };

        config.Enabled.Should().BeFalse();
        config.SuggestThreshold.Should().Be(0.6);
        config.AutoApplyThreshold.Should().Be(0.85);
        config.AutonomousThreshold.Should().Be(0.95);
        config.SentimentGating.Should().BeTrue();
        config.DailyTokenBudget.Should().BeNull();
    }

    // ─── JSON round-trip ──────────────────────────────────────────────────────

    [Fact]
    public void AiConfig_ShouldRoundTripThroughJson_WithNewShape()
    {
        var original = new TypificationAiConfig
        {
            Enabled = true,
            Mode = AiMode.AutoFill,
            SuggestThreshold = 0.6,
            AutoApplyThreshold = 0.88,
            AutonomousThreshold = 0.97,
            Autonomous = true,
            SentimentGating = true,
            DailyTokenBudget = 50_000L,
            EntityFieldMap = new Dictionary<string, string> { ["intent"] = "outcome_notes" },
        };

        var json = JsonSerializer.Serialize(original, AiConfigTestContext.Default.TypificationAiConfig);
        var restored = JsonSerializer.Deserialize(json, AiConfigTestContext.Default.TypificationAiConfig);

        restored.Should().NotBeNull();
        restored!.Enabled.Should().BeTrue();
        restored.Mode.Should().Be(AiMode.AutoFill);
        restored.SuggestThreshold.Should().Be(0.6);
        restored.AutoApplyThreshold.Should().Be(0.88);
        restored.AutonomousThreshold.Should().Be(0.97);
        restored.Autonomous.Should().BeTrue();
        restored.SentimentGating.Should().BeTrue();
        restored.DailyTokenBudget.Should().Be(50_000L);
        restored.EntityFieldMap.Should().ContainKey("intent").WhoseValue.Should().Be("outcome_notes");
    }

    [Fact]
    public void AiConfig_ShouldRoundTripNullDailyTokenBudget()
    {
        var original = new TypificationAiConfig
        {
            Enabled = false,
            Mode = AiMode.Off,
            DailyTokenBudget = null,
            EntityFieldMap = new Dictionary<string, string>(),
        };

        var json = JsonSerializer.Serialize(original, AiConfigTestContext.Default.TypificationAiConfig);
        var restored = JsonSerializer.Deserialize(json, AiConfigTestContext.Default.TypificationAiConfig);

        restored.Should().NotBeNull();
        restored!.DailyTokenBudget.Should().BeNull();
    }
}

/// <summary>
/// Minimal source-generated JSON context used only by <see cref="TypificationAiConfigTests"/>.
/// Tests do not ship as AOT binaries so reflection-mode fallback would also work; we use
/// source-gen to prove the type is serializable without reflection in the same way
/// <c>PostgresJsonContext</c> does in production.
/// </summary>
[JsonSerializable(typeof(TypificationAiConfig))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class AiConfigTestContext : JsonSerializerContext;
