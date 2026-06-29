using System.Text;

namespace Verbara.Platform.Governance.Tests;

/// <summary>
/// In-process regression guard: parses every test source file with Roslyn and fails the build if a
/// wall-clock synchronization barrier is added without a valid inline <c>// fence-allow:</c> marker.
/// Includes liveness self-tests (the scan must actually walk a large file set) and detector unit
/// tests that pin both true positives and the prose/string false-positive immunity.
/// </summary>
public sealed class SyncFenceRegressionGuardTests
{
    // Conservative floor: the real test-file count is ~496 (measured 2026-06-29). A floor well
    // below that defeats the "found zero files -> false green" failure mode while tolerating churn.
    private const int MinimumScannedFiles = 300;

    [Fact]
    public void Guard_ShouldFindNoUnmarkedFences_InTestTree()
    {
        var repoRoot = Directory.GetParent(TestTreeSource.TestsRoot())!.FullName;
        var violations = new List<FenceViolation>();

        foreach (var file in TestTreeSource.EnumerateTestSources())
        {
            var source = File.ReadAllText(file);
            var relative = Path.GetRelativePath(repoRoot, file);
            violations.AddRange(SyncFenceScanner.Scan(source, relative));
        }

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingTestTree()
    {
        var count = TestTreeSource.EnumerateTestSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real test tree; a near-zero count means the locator broke and " +
            "the fence scan would be a false green");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenUnmarkedTaskDelay()
    {
        const string source = "class C { async System.Threading.Tasks.Task M() { await Task.Delay(5); } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Task.Delay");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenUnmarkedThreadSleep()
    {
        const string source = "class C { void M() { Thread.Sleep(100); } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Thread.Sleep");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenFullyQualifiedThreadSleep()
    {
        const string source = "class C { void M() { System.Threading.Thread.Sleep(1); } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Thread.Sleep");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTaskDelayInComment()
    {
        const string source = "class C { void M() { /* waited via Task.Delay earlier */ } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTaskDelayInXmlDoc()
    {
        const string source =
            "/// <c>Task.Delay</c> guess\n" +
            "class C { void M() { } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTaskDelayInStringLiteral()
    {
        const string source = "class C { void M() { var s = \"call Task.Delay( now\"; } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenWellFormedMarkerOnSameLine()
    {
        const string source =
            "class C { async System.Threading.Tasks.Task M() {\n" +
            "    await Task.Delay(5); // fence-allow: SETTLE — wait for TTL\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenMarkerOnPrecedingLine()
    {
        const string source =
            "class C { async System.Threading.Tasks.Task M() {\n" +
            "    // fence-allow: SETTLE — wait for TTL\n" +
            "    await Task.Delay(5);\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenBareMarkerHasNoCategory()
    {
        const string source =
            "class C { async System.Threading.Tasks.Task M() {\n" +
            "    await Task.Delay(5); // fence-allow:\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Task.Delay");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMarkerCategoryUnknown()
    {
        const string source =
            "class C { async System.Threading.Tasks.Task M() {\n" +
            "    await Task.Delay(5); // fence-allow: WHATEVER — y\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Task.Delay");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMarkerHasNoReason()
    {
        const string source =
            "class C { async System.Threading.Tasks.Task M() {\n" +
            "    await Task.Delay(5); // fence-allow: SETTLE\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Task.Delay");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenStopwatchSpinLoop()
    {
        const string source =
            "class C { void M(System.Diagnostics.Stopwatch sw) {\n" +
            "    while (sw.Elapsed < System.TimeSpan.FromSeconds(1)) { }\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Stopwatch.spin");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenStaticThreadImport()
    {
        const string source =
            "using static System.Threading.Thread;\n" +
            "class C { void M() { } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("ThreadingAlias");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTaskAliasImport()
    {
        const string source =
            "using T = System.Threading.Tasks.Task;\n" +
            "class C { void M() { } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("ThreadingAlias");
    }

    private static string BuildFailureMessage(List<FenceViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append("found ").Append(violations.Count)
            .AppendLine(" unmarked wall-clock synchronization barrier(s) in the test tree:");
        foreach (var v in violations.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line))
        {
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line)
                .Append("  ").Append(v.Api).Append("  ").AppendLine(v.Detail);
        }

        sb.AppendLine(
            "Each site must either remove the wall-clock barrier or annotate with " +
            "// fence-allow: CATEGORY — reason (CATEGORY ∈ SIMULATED-WORK | GUARD-TIMEOUT | SETTLE | LOOP-DRIVER).");
        return sb.ToString();
    }
}
