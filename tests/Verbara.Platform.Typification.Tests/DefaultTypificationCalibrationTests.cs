using NSubstitute;
using Verbara.Platform.Core;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Typification.Tests;

public sealed class DefaultTypificationCalibrationTests
{
    private static readonly TenantId Tenant = new("tenant-calib");
    private static readonly EntityId SchemaId = EntityId.From("schema-calib");

    // ── Schema factory ────────────────────────────────────────────────────────

    private static TypificationSchema Schema(
        double autoApplyThreshold = 0.85,
        double autonomousThreshold = 0.95) =>
        new()
        {
            SchemaId = SchemaId,
            TenantId = Tenant,
            Name = "calibration-test",
            Version = 1,
            IsPublished = true,
            Nodes = [],
            Fields = [],
            DataDips = [],
            AiConfig = new TypificationAiConfig
            {
                Enabled = true,
                AutoApplyThreshold = autoApplyThreshold,
                AutonomousThreshold = autonomousThreshold,
                EntityFieldMap = new Dictionary<string, string>(),
            },
        };

    // ── SUT factory ───────────────────────────────────────────────────────────

    private static (DefaultTypificationCalibration Sut, ITypificationSchemaStore SchemaStore, IAiSuggestionStore SuggestionStore)
        BuildSut(TypificationSchema? schema, (int Samples, double AcceptRate) autoApplyResult, (int Samples, double AcceptRate) autonomousResult)
    {
        var schemaStore = Substitute.For<ITypificationSchemaStore>();
        schemaStore
            .GetLatestPublishedAsync(Tenant, SchemaId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(schema));

        var suggestionStore = Substitute.For<IAiSuggestionStore>();

        // Wire up both confidence-band calls by argument matching on the threshold.
        if (schema is not null)
        {
            var tenantEntityId = EntityId.From(Tenant.Value);

            suggestionStore
                .QueryAccuracyAsync(tenantEntityId, SchemaId, schema.AiConfig.AutoApplyThreshold, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(autoApplyResult));

            suggestionStore
                .QueryAccuracyAsync(tenantEntityId, SchemaId, schema.AiConfig.AutonomousThreshold, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(autonomousResult));
        }

        var sut = new DefaultTypificationCalibration(schemaStore, suggestionStore);
        return (sut, schemaStore, suggestionStore);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_ShouldReportNotReady_WhenBelowMinSamples()
    {
        // 199 samples at 0.9 accuracy — one below the 200-sample minimum.
        var (sut, _, _) = BuildSut(
            schema: Schema(),
            autoApplyResult: (199, 0.90),
            autonomousResult: (199, 0.96));

        var status = await sut.GetStatusAsync(Tenant, SchemaId, CancellationToken.None);

        status.AutoFillReady.Should().BeFalse();
        status.AutonomousReady.Should().BeFalse();
        status.Samples.Should().Be(199);
        status.Accuracy.Should().BeApproximately(0.90, 1e-9);
    }

    [Fact]
    public async Task GetStatus_ShouldReportAutoFillReady_WhenSamplesAndAccuracyClear()
    {
        // 200 samples at exactly 0.85 accuracy at the AutoApply band → AutoFillReady.
        // Autonomous band does not reach 0.95 → AutonomousReady stays false.
        var (sut, _, _) = BuildSut(
            schema: Schema(autoApplyThreshold: 0.85, autonomousThreshold: 0.95),
            autoApplyResult: (200, 0.85),
            autonomousResult: (200, 0.90));

        var status = await sut.GetStatusAsync(Tenant, SchemaId, CancellationToken.None);

        status.AutoFillReady.Should().BeTrue();
        status.AutonomousReady.Should().BeFalse();
        status.Samples.Should().Be(200);
        status.Accuracy.Should().BeApproximately(0.85, 1e-9);
    }

    [Fact]
    public async Task GetStatus_ShouldReportAutonomousReady_OnlyWhenAccuracyClearsHigherBar()
    {
        // Part 1: 200 samples, accuracy 0.90 at the AutoApply band → AutoFillReady only.
        var (sutA, _, _) = BuildSut(
            schema: Schema(),
            autoApplyResult: (200, 0.90),
            autonomousResult: (200, 0.90));

        var statusA = await sutA.GetStatusAsync(Tenant, SchemaId, CancellationToken.None);

        statusA.AutoFillReady.Should().BeTrue("accuracy 0.90 ≥ MinCalibrationAccuracy 0.85");
        statusA.AutonomousReady.Should().BeFalse("accuracy 0.90 < MinAutonomousAccuracy 0.95");

        // Part 2: same sample count but accuracy 0.96 at the Autonomous band → both true.
        var (sutB, _, _) = BuildSut(
            schema: Schema(),
            autoApplyResult: (200, 0.96),
            autonomousResult: (200, 0.96));

        var statusB = await sutB.GetStatusAsync(Tenant, SchemaId, CancellationToken.None);

        statusB.AutoFillReady.Should().BeTrue();
        statusB.AutonomousReady.Should().BeTrue("accuracy 0.96 ≥ MinAutonomousAccuracy 0.95");
    }

    [Fact]
    public async Task GetStatus_ShouldReportNotReady_WhenSchemaMissing()
    {
        var (sut, _, _) = BuildSut(
            schema: null,
            autoApplyResult: (0, 0),
            autonomousResult: (0, 0));

        var status = await sut.GetStatusAsync(Tenant, SchemaId, CancellationToken.None);

        status.AutoFillReady.Should().BeFalse();
        status.AutonomousReady.Should().BeFalse();
        status.Samples.Should().Be(0);
        status.Accuracy.Should().Be(0);
    }

    [Fact]
    public async Task GetStatus_ShouldReportAutonomousReady_WhenAccuracyIsAtExactBoundary()
    {
        // Both bands return exactly (200, 0.95) — verifies the >= 0.95 inclusive boundary
        // for AutonomousReady and the >= 0.85 inclusive boundary for AutoFillReady.
        var (sut, _, _) = BuildSut(
            schema: Schema(autoApplyThreshold: 0.85, autonomousThreshold: 0.95),
            autoApplyResult: (200, 0.95),
            autonomousResult: (200, 0.95));

        var status = await sut.GetStatusAsync(Tenant, SchemaId, CancellationToken.None);

        status.AutoFillReady.Should().BeTrue("accuracy 0.95 ≥ MinCalibrationAccuracy 0.85");
        status.AutonomousReady.Should().BeTrue("accuracy 0.95 == MinAutonomousAccuracy 0.95 (inclusive boundary)");
    }
}
