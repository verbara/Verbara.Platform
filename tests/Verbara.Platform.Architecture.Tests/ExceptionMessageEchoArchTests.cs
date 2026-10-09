namespace Verbara.Platform.Architecture.Tests;

/// <summary>
/// v2.27.0 block F (D13) — no endpoint in the API host builds an error response from a caught
/// exception's message. Exception text can name tables, ids, provider responses or internal state;
/// it goes to the log, and the caller gets a stable message (or a platform error code) instead.
/// <c>ErrorHandlingMiddleware</c> covers exceptions that escape the endpoint; this guard covers the
/// ones an endpoint catches itself. Self-tests pin the scanner's true and false positives.
/// </summary>
public sealed class ExceptionMessageEchoArchTests
{
    // The Api endpoint tree is ~90+ files; a floor well below that defeats "scanned nothing -> green".
    private const int MinimumScannedFiles = 40;

    private static readonly string ApiProjectSegment =
        Path.DirectorySeparatorChar + "Verbara.Platform.Api" + Path.DirectorySeparatorChar;

    [Fact]
    public void Endpoints_ShouldNotCopyExceptionMessage_IntoErrorResponses()
    {
        var repoRoot = SourceTreeSource.RepoRoot();
        var files = SourceTreeSource.EnumerateEndpointSources()
            .Where(f => f.Contains(ApiProjectSegment, StringComparison.Ordinal))
            .ToList();

        files.Count.Should().BeGreaterThan(MinimumScannedFiles,
            "the guard must walk the real Api endpoint tree; a near-zero count is a false green");

        var matches = files
            .SelectMany(f => ExceptionMessageEchoScanner.Scan(File.ReadAllText(f), Path.GetRelativePath(repoRoot, f)))
            .ToList();

        matches.Should().BeEmpty(
            "an endpoint must not copy an exception's message into an error response; log it and return a " +
            "stable message or a platform error code instead. Offenders:\n" +
            string.Join('\n', matches.Select(m => $"  {m.Path}:{m.Line}  {m.Expression}")));
    }

    [Theory]
    [InlineData("catch (InvalidOperationException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }")]
    [InlineData("catch (InvalidOperationException ex) { return TypedResults.BadRequest(new ErrorResponse(ex.Message)); }")]
    [InlineData("catch (Exception ex) { return Results.BadRequest(new ErrorResponse($\"failed: {ex.Message}\")); }")]
    [InlineData("catch (Exception e) { return Results.Problem(detail: e.Message); }")]
    [InlineData("catch (Exception exception) { return Results.BadRequest(exception.Message); }")]
    [InlineData("catch (Exception ex) { var p = new ProblemDetails { Detail = ex.InnerException!.Message }; return Results.Json(p); }")]
    [InlineData("catch (Exception ex) { var r = new ErrorResponse(ex.Message); return Results.Json(r); }")]
    public void Scan_ShouldFlag_WhenCatchVariableMessageBuildsAnErrorResponse(string catchClause)
    {
        var source = "class C { IResult M() { try { return Results.Ok(); } " + catchClause + " } }";

        ExceptionMessageEchoScanner.Scan(source, "x.cs").Should().ContainSingle();
    }

    [Theory]
    [InlineData("catch (Exception ex) { logger.LogWarning(ex, \"failed {M}\", ex.Message); return Results.BadRequest(new ErrorResponse(\"Upload rejected.\")); }")]
    [InlineData("catch (Exception ex) { outcomes.Add(new OutcomeDto { ErrorMessage = ex.Message }); return Results.Ok(); }")]
    [InlineData("catch (Exception ex) { return Results.BadRequest(new ErrorResponse(result.Message)); }")]
    [InlineData("catch (Exception ex) { // Results.BadRequest(new ErrorResponse(ex.Message))\n return Results.BadRequest(); }")]
    [InlineData("catch (Exception ex) { return Results.BadRequest(new ErrorResponse(\"ex.Message\")); }")]
    public void Scan_ShouldIgnore_WhenMessageDoesNotReachAnErrorResponse(string catchClause)
    {
        var source = "class C { IResult M() { try { return Results.Ok(); } " + catchClause + " } }";

        ExceptionMessageEchoScanner.Scan(source, "x.cs").Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldNameFileAndLine_WhenFlagging()
    {
        const string source =
            "class C { IResult M() {\n" +
            "  try { return Results.Ok(); }\n" +
            "  catch (InvalidOperationException ex)\n" +
            "  { return Results.BadRequest(new ErrorResponse(ex.Message)); }\n" +
            "} }";

        var match = ExceptionMessageEchoScanner.Scan(source, "Endpoints/Foo.cs").Should().ContainSingle().Subject;

        match.Path.Should().Be("Endpoints/Foo.cs");
        match.Line.Should().Be(4);
    }
}
