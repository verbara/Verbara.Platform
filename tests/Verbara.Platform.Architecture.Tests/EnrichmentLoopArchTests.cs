namespace Verbara.Platform.Architecture.Tests;

/// <summary>
/// ADR-0012 Ola-3 Gate 3 — the enrichment-loop (N+1) architecture test. After the ANALYTICS
/// enrichment path (<c>AnalyticsEndpoints</c>) was migrated onto the batch
/// <c>GetByIdsAsync</c>/<c>GetBySessionIdsAsync</c> primitives, ZERO
/// <c>await store.GetAsync(...)</c> / <c>await store.GetByIdAsync(...)</c> point reads may remain
/// inside a loop there. This guard fails if a new per-row store read creeps back into a loop on the
/// analytics surface; the fix is to hoist the loop body onto a batch primitive (or, where genuinely
/// unavoidable, annotate the statement <c>// enrichment-n1-ok: &lt;reason&gt;</c>). Self-tests pin
/// the scanner's true/false positives.
/// </summary>
/// <remarks>
/// <b>Scope is the analytics enrichment surface SPECIFICALLY.</b> A blanket "no single-item read in a
/// loop anywhere under Endpoints/" floor is out of scope for this gate — the same way Gate-2
/// (<see cref="ServiceLocatorArchTests"/>) targets <c>IRealtimeSyncService</c> and not every
/// <c>RequestServices.GetService</c>. A full-tree sweep surfaces several PRE-EXISTING N+1 loops in
/// endpoints this ADR does not touch (a sequential tenant-hierarchy walk that cannot be batched, a
/// memoized per-actor-tenant read, an enum-cardinality channel-config loop, and several
/// <c>queueStore.GetByIdAsync</c> loops with no batch primitive yet). Those are recorded in
/// <see cref="KnownOutOfScopeN1Files"/> as tracked debt for a future ADR; this gate holds the line on
/// the surface Gate 3 actually collapsed. The walk-many-files liveness still walks the WHOLE endpoint
/// tree so a broken locator can never present as a false green.
/// </remarks>
public sealed class EnrichmentLoopArchTests
{
    // Conservative floor: the endpoint tree is ~90+ files. Reuses the Gate-2 minimum so a broken
    // locator (scanning zero files) can never present as a false green.
    private const int MinimumScannedFiles = 40;

    // The analytics enrichment surface Gate 3 collapsed — the ONLY file the zero-floor governs.
    private const string AnalyticsEndpointsFile = "AnalyticsEndpoints.cs";

    // Pre-existing N+1 loops OUTSIDE this ADR's charter (see class remarks). Tracked as debt; a
    // future ADR either adds the missing batch primitive (queueStore/tenantStore) or annotates the
    // genuinely-sequential walks. Listed here so the full-tree assertion documents — rather than
    // silently tolerates — the known debt, and fails loudly if the set drifts.
    private static readonly string[] KnownOutOfScopeN1Files =
    [
        "ChannelConfigEndpoints.cs",
        "AdminEndpoints.cs",
        "SupervisorEndpoints.cs",
        "QueueMembersEndpoints.cs",
        "ManagementImpersonationEndpoints.cs",
    ];

    [Fact]
    public void EndpointScan_ShouldWalkManyFiles_WhenEnumeratingSrc()
    {
        var count = SourceTreeSource.EnumerateEndpointSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real endpoint tree; a near-zero count means the locator broke " +
            "and the enrichment-loop scan would be a false green");
    }

    [Fact]
    public void EnrichmentLoops_ShouldBeZero_OnAnalyticsSurface()
    {
        var repoRoot = SourceTreeSource.RepoRoot();
        var matches = new List<EnrichmentLoopMatch>();

        foreach (var file in SourceTreeSource.EnumerateEndpointSources())
        {
            if (!string.Equals(Path.GetFileName(file), AnalyticsEndpointsFile, StringComparison.Ordinal))
                continue;

            var source = File.ReadAllText(file);
            var relative = Path.GetRelativePath(repoRoot, file);
            matches.AddRange(EnrichmentLoopScanner.Scan(source, relative));
        }

        var sites = string.Join("; ", matches.Select(m => $"{m.Path}:{m.Line} ({m.EnclosingMethod} → {m.Method})"));
        matches.Should().BeEmpty(
            $"no single-item store read (GetAsync/GetByIdAsync) may be awaited inside a loop in {AnalyticsEndpointsFile} — " +
            "Gate 3 collapsed those onto GetByIdsAsync/GetBySessionIdsAsync; hoist any new one onto a batch primitive. " +
            $"Found: {sites}");
    }

    [Fact]
    public void EnrichmentLoops_OutsideAnalytics_ShouldStayWithinKnownDebt()
    {
        // Documents (does not silently tolerate) the pre-existing out-of-scope N+1 loops: every
        // full-tree match must live in a KnownOutOfScopeN1Files file. A match in any OTHER file means
        // a new N+1 crept into an endpoint — either add the batch primitive or, if this gate should
        // now cover it, promote that file. This is tracked debt, not a green light.
        var repoRoot = SourceTreeSource.RepoRoot();
        var offenders = new List<EnrichmentLoopMatch>();

        foreach (var file in SourceTreeSource.EnumerateEndpointSources())
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, AnalyticsEndpointsFile, StringComparison.Ordinal))
                continue;
            if (KnownOutOfScopeN1Files.Contains(name, StringComparer.Ordinal))
                continue;

            var source = File.ReadAllText(file);
            var relative = Path.GetRelativePath(repoRoot, file);
            offenders.AddRange(EnrichmentLoopScanner.Scan(source, relative));
        }

        var sites = string.Join("; ", offenders.Select(m => $"{m.Path}:{m.Line} ({m.EnclosingMethod} → {m.Method})"));
        offenders.Should().BeEmpty(
            "a single-item store read is awaited inside a loop in an endpoint that is NOT on the analytics " +
            "surface and NOT in the tracked out-of-scope debt list — hoist it onto a batch primitive, or add " +
            $"the file to KnownOutOfScopeN1Files with a rationale if it is genuinely unavoidable. Found: {sites}");
    }

    // ─── Scanner self-tests ──────────────────────────────────────────────────────

    [Fact]
    public void Scan_ShouldFlag_WhenGetByIdAsyncInForeach()
    {
        const string source =
            "class C { async Task M() {\n" +
            "    foreach (var id in ids) {\n" +
            "        var a = await agentStore.GetByIdAsync(t, id, ct);\n" +
            "    }\n" +
            "} }";

        var matches = EnrichmentLoopScanner.Scan(source, "x.cs");

        matches.Should().ContainSingle();
        matches[0].Method.Should().Be("GetByIdAsync");
        matches[0].EnclosingMethod.Should().Be("M");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenGetAsyncInForeach()
    {
        const string source =
            "class C { async Task M() {\n" +
            "    foreach (var row in rows) {\n" +
            "        var qa = await qaStore.GetAsync(row.SessionId, t, ct);\n" +
            "    }\n" +
            "} }";

        var matches = EnrichmentLoopScanner.Scan(source, "x.cs");

        matches.Should().ContainSingle().Which.Method.Should().Be("GetAsync");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenQueryAsyncInForeach()
    {
        // Bulk verb — CdrEnricher-style QueryAsync is a set read, not a per-row N+1.
        const string source =
            "class C { async Task M() {\n" +
            "    foreach (var x in xs) {\n" +
            "        var rows = await cdrStore.QueryAsync(t, query, ct);\n" +
            "    }\n" +
            "} }";

        var matches = EnrichmentLoopScanner.Scan(source, "x.cs");

        matches.Should().BeEmpty("Query* is a bulk read, not a single-item point read");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenSaveAsyncInForeach()
    {
        const string source =
            "class C { async Task M() {\n" +
            "    foreach (var a in agents) {\n" +
            "        await agentStore.SaveAsync(a, ct);\n" +
            "    }\n" +
            "} }";

        var matches = EnrichmentLoopScanner.Scan(source, "x.cs");

        matches.Should().BeEmpty("writes (Save/Delete/Update/Upsert) are out of scope");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenGetAsyncNotInLoop()
    {
        const string source =
            "class C { async Task M() {\n" +
            "    var qa = await qaStore.GetAsync(sessionId, t, ct);\n" +
            "} }";

        var matches = EnrichmentLoopScanner.Scan(source, "x.cs");

        matches.Should().BeEmpty("a single point read outside a loop is not an N+1");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenSuppressedByMarker()
    {
        const string source =
            "class C { async Task M() {\n" +
            "    foreach (var row in rows) {\n" +
            "        var qa = await qaStore.GetAsync(row.SessionId, t, ct); // enrichment-n1-ok: legacy pending batch API\n" +
            "    }\n" +
            "} }";

        var matches = EnrichmentLoopScanner.Scan(source, "x.cs");

        matches.Should().BeEmpty("a // enrichment-n1-ok: <reason> marker suppresses the finding");
    }

    [Fact]
    public void Scan_ShouldStillFlag_WhenSuppressionMarkerHasEmptyReason()
    {
        // An empty reason after the marker does NOT suppress — the guard still fires.
        const string source =
            "class C { async Task M() {\n" +
            "    foreach (var row in rows) {\n" +
            "        var qa = await qaStore.GetAsync(row.SessionId, t, ct); // enrichment-n1-ok:\n" +
            "    }\n" +
            "} }";

        var matches = EnrichmentLoopScanner.Scan(source, "x.cs");

        matches.Should().ContainSingle("an empty-reason marker must not suppress the finding");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenGetByIdsBatchInLoop()
    {
        // The batch verb is the FIX — it must never be flagged, even inside a loop.
        const string source =
            "class C { async Task M() {\n" +
            "    foreach (var page in pages) {\n" +
            "        var agents = await agentStore.GetByIdsAsync(t, ids, ct);\n" +
            "    }\n" +
            "} }";

        var matches = EnrichmentLoopScanner.Scan(source, "x.cs");

        matches.Should().BeEmpty("batch verbs (GetByIdsAsync/GetBySessionIdsAsync) are the fix, not the hazard");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenMentionInCommentOrString()
    {
        const string source =
            "class C { async Task M() {\n" +
            "    foreach (var x in xs) {\n" +
            "        // await store.GetAsync(x, t, ct) was the old N+1\n" +
            "        var s = \"await store.GetByIdAsync(x)\";\n" +
            "    }\n" +
            "} }";

        var matches = EnrichmentLoopScanner.Scan(source, "x.cs");

        matches.Should().BeEmpty("comments and string literals can never be a syntactic match");
    }
}
