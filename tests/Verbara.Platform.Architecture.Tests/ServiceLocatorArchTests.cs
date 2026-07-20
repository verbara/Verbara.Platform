namespace Verbara.Platform.Architecture.Tests;

/// <summary>
/// ADR-0012 Ola-3 Gate 2 — the Service-Locator architecture test. After the queue / agent /
/// queue-member realtime sync was migrated into the store decorators, exactly ONE
/// <c>RequestServices.GetService&lt;IRealtimeSyncService&gt;()</c> resolve may remain under the
/// endpoint surface: the presence path (<c>SetPausedAsync</c> → <c>SyncAgentPausedAsync</c>), which
/// is NOT a store write and so has no storage seam to ride. This guard fails if a new realtime
/// Service-Locator resolve creeps back in (count &gt; 1) AND if the last one disappears without the
/// allowlist being updated (count == 0), which forces the cleanup when a future
/// <c>IRealtimeAgentPresence</c> seam lands. Self-tests pin the scanner's true/false positives.
/// </summary>
public sealed class ServiceLocatorArchTests
{
    // Conservative floor: the endpoint tree is ~90+ files. A floor well below that defeats the
    // "found zero files -> false green" failure mode while tolerating churn.
    private const int MinimumScannedFiles = 40;

    // Belt-and-suspenders allowlist. Both are already OUTSIDE the Endpoints/ glob (they live under
    // Services/ and the composition root), so the scan never reaches them — the explicit exclusion
    // documents intent and survives a future file move.
    private static readonly string[] AllowlistedFileNames =
    [
        "RealtimeReconciliationService.cs",
        "Program.cs",
    ];

    // The single tolerated survivor: file + the SDK method it must resolve within a small window.
    private const string PresenceFile = "QueueMembersEndpoints.cs";
    private const string PresenceSdkMethod = "SyncAgentPausedAsync";

    [Fact]
    public void EndpointScan_ShouldWalkManyFiles_WhenEnumeratingSrc()
    {
        var count = SourceTreeSource.EnumerateEndpointSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real endpoint tree; a near-zero count means the locator broke " +
            "and the Service-Locator scan would be a false green");
    }

    [Fact]
    public void RealtimeServiceLocator_ShouldSurviveOnlyOnPresencePath_UnderEndpoints()
    {
        var repoRoot = SourceTreeSource.RepoRoot();
        var matches = new List<ServiceLocatorMatch>();

        foreach (var file in SourceTreeSource.EnumerateEndpointSources())
        {
            if (AllowlistedFileNames.Contains(Path.GetFileName(file), StringComparer.Ordinal))
                continue;

            var source = File.ReadAllText(file);
            var relative = Path.GetRelativePath(repoRoot, file);
            matches.AddRange(ServiceLocatorScanner.Scan(source, relative));
        }

        // Exactly one — count > 1 means a new realtime Service-Locator resolve crept back in
        // (migrate it into a store decorator); count == 0 means the presence site was removed
        // without deleting this allowlist entry (delete the carve-out when IRealtimeAgentPresence lands).
        matches.Should().ContainSingle(BuildFailureMessage(matches));

        var survivor = matches[0];
        Path.GetFileName(survivor.Path).Should().Be(
            PresenceFile,
            "the only tolerated realtime Service-Locator resolve is the presence path");

        // "The enclosing method resolves SyncAgentPausedAsync" — assert the SDK presence call
        // sits within ~5 lines of the resolve (the presence handler resolves the service then
        // immediately calls SyncAgentPausedAsync).
        var survivorFile = Path.Combine(repoRoot, survivor.Path);
        var lines = File.ReadAllLines(survivorFile);
        var lo = Math.Max(0, survivor.Line - 1 - 5);
        var hi = Math.Min(lines.Length, survivor.Line - 1 + 6);
        var window = string.Join('\n', lines[lo..hi]);
        window.Should().Contain(
            PresenceSdkMethod,
            $"the surviving resolve must be the presence path (a {PresenceSdkMethod} call within ~5 lines)");
    }

    // ─── Scanner self-tests (mirror SyncFenceRegressionGuardTests) ───────────────

    [Fact]
    public void Scan_ShouldFlag_WhenGetServiceRealtimeSyncService()
    {
        const string source =
            "class C { void M(HttpContext context) {\n" +
            "    var s = context.RequestServices.GetService<IRealtimeSyncService>();\n" +
            "} }";

        var matches = ServiceLocatorScanner.Scan(source, "x.cs");

        matches.Should().ContainSingle().Which.EnclosingMethod.Should().Be("M");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenGetRequiredServiceRealtimeSyncService()
    {
        const string source =
            "class C { void M(HttpContext context) {\n" +
            "    var s = context.RequestServices.GetRequiredService<IRealtimeSyncService>();\n" +
            "} }";

        var matches = ServiceLocatorScanner.Scan(source, "x.cs");

        matches.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenCleanFileWithNoRealtimeResolve()
    {
        const string source =
            "class C { void M(HttpContext context) {\n" +
            "    var q = context.RequestServices.GetService<IQueueStore>();\n" +
            "} }";

        var matches = ServiceLocatorScanner.Scan(source, "x.cs");

        matches.Should().BeEmpty("only IRealtimeSyncService resolves are in scope");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenMentionInComment()
    {
        const string source =
            "class C { void M() {\n" +
            "    // context.RequestServices.GetService<IRealtimeSyncService>() moved to a decorator\n" +
            "} }";

        var matches = ServiceLocatorScanner.Scan(source, "x.cs");

        matches.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenMentionInStringLiteral()
    {
        const string source =
            "class C { void M() {\n" +
            "    var s = \"RequestServices.GetService<IRealtimeSyncService>()\";\n" +
            "} }";

        var matches = ServiceLocatorScanner.Scan(source, "x.cs");

        matches.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlagExactlyOne_ForThePresenceShape()
    {
        // The allowlisted survivor shape: resolve the sync service in the presence handler,
        // then call SyncAgentPausedAsync. The scanner reports the single resolve; the arch
        // test's window check confirms the adjacent SyncAgentPausedAsync call.
        const string source =
            "class Q { void SetPausedAsync(HttpContext context) {\n" +
            "    var syncService = context.RequestServices.GetService<IRealtimeSyncService>();\n" +
            "    if (syncService is not null) { syncService.SyncAgentPausedAsync(\"t\", \"a\", true, default); }\n" +
            "} }";

        var matches = ServiceLocatorScanner.Scan(source, "x.cs");

        matches.Should().ContainSingle().Which.EnclosingMethod.Should().Be("SetPausedAsync");
    }

    private static string BuildFailureMessage(List<ServiceLocatorMatch> matches)
    {
        if (matches.Count == 0)
        {
            return "found ZERO RequestServices.GetService<IRealtimeSyncService>() resolves under Endpoints/ — " +
                "the presence carve-out (QueueMembersEndpoints.SetPausedAsync → SyncAgentPausedAsync) was " +
                "removed. Delete this allowlisted expectation (and update the arch test) now that a store " +
                "seam covers presence too.";
        }

        var sites = string.Join("; ", matches.Select(m => $"{m.Path}:{m.Line} ({m.EnclosingMethod})"));
        return "expected EXACTLY ONE realtime Service-Locator resolve under Endpoints/ (the presence path), " +
            $"but found {matches.Count}: {sites}. A new context.RequestServices.GetService<IRealtimeSyncService>() " +
            "crept in — migrate the sync into the relevant store decorator (RealtimeSyncing*Store) instead.";
    }
}
