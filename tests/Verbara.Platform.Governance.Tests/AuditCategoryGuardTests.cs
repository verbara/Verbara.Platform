namespace Verbara.Platform.Governance.Tests;

/// <summary>
/// audit-categories-accepted (#694): every audit category the code writes is one <c>audit_entries_category_check</c>
/// accepts once every migration has run. A category outside it makes Postgres refuse the audit write (23514) after the
/// change it records was saved, so the caller answers 500 for work that was done. The accepted set is read from the
/// migrations and the written set from <c>src/</c>, so adding a category on either side alone fails here.
/// </summary>
public sealed class AuditCategoryGuardTests
{
    private const string MigrationsDirectory = "src/Verbara.Platform.Storage.Postgres/Migrations";
    private const string AuditEntryFile = "src/Verbara.Platform.Audit/AuditEntry.cs";
    private const string LiveDbTestFile = "tests/Verbara.Platform.Storage.Postgres.Tests/Stores/AuditCategoryMigrationTests.cs";

    /// <summary>
    /// Places that hand on a category they did not choose, so they add no value of their own: each is checked to still
    /// exist, so an entry cannot outlive its code.
    /// </summary>
    private static readonly (string File, string Expression, string Reason)[] PassThroughs =
    [
        ("src/Verbara.Platform.Audit/DefaultAuditService.cs", "category",
            "RecordAsync stores its own category parameter; its callers are scanned"),
        ("src/Verbara.Platform.Audit/AutonomousAuditRedaction.cs", "entry.Category",
            "a redacted copy keeps the category of the entry it copies"),
        ("src/Verbara.Platform.Storage.Postgres/Stores/PostgresAuditStore.cs", "category ?? \"config\"",
            "maps a stored row back to an entry; it writes nothing"),
    ];

    private static string Root => TestTreeSource.RepoRoot();

    private static IReadOnlySet<string> Accepted() =>
        AuditCategoryScanner.AcceptedCategories(
            Directory.EnumerateFiles(Path.Combine(Root, MigrationsDirectory), "*.sql")
                .Select(f => (Path.GetFileName(f), File.ReadAllText(f))));

    private static List<AuditCategoryWrite> Writes() =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .SelectMany(f => AuditCategoryScanner.CategoryWrites(File.ReadAllText(f), Path.GetRelativePath(Root, f).Replace('\\', '/')))
            .ToList();

    private static bool IsPassThrough(AuditCategoryWrite write) =>
        write.Value is null && PassThroughs.Any(p => p.File == write.File && p.Expression == write.Expression);

    [Fact]
    public void CodeWrites_ShouldUseOnlyCategoriesTheConstraintAccepts_WhenEveryMigrationIsApplied()
    {
        var accepted = Accepted();

        var refused = Writes()
            .Where(w => !IsPassThrough(w) && (w.Value is null || !accepted.Contains(w.Value)))
            .Select(w => w.ToString())
            .ToList();

        string.Join(Environment.NewLine, refused).Should().BeEmpty(
            "audit_entries_category_check accepts only [{0}]; a write outside it is refused by Postgres (23514) after the " +
            "change it records was saved — add the category in a new migration or use an accepted one, and pass a " +
            "category this guard can read (a literal, a const string, or a method of the same file returning literals)",
            string.Join(", ", accepted.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Scan_ShouldFindTheKnownWriters_WhenReadingTheTree()
    {
        var writes = Writes();

        writes.Should().HaveCountGreaterThan(100, "the tree writes audit entries from well over a hundred places");
        writes.Should().Contain(w => w.File.EndsWith("CsatResponseEndpoints.cs", StringComparison.Ordinal) && w.Value == "csat");
        writes.Should().Contain(w => w.File.EndsWith("ContactAuditRecord.cs", StringComparison.Ordinal) && w.Value == "contact",
            "a const string category is resolved");
        writes.Should().Contain(w => w.File.EndsWith("DefaultAuditService.cs", StringComparison.Ordinal) && w.Value == "data_access",
            "the category the legacy LogAsync infers from the action is resolved through its method");
    }

    [Fact]
    public void PassThroughs_ShouldEachStillExist_WhenReadingTheTree()
    {
        var writes = Writes();

        foreach (var (file, expression, reason) in PassThroughs)
            writes.Where(w => w.File == file && w.Expression == expression && w.Value == null).Should().NotBeEmpty(
                "the pass-through '{0}' in {1} ({2}) is listed, so it must still be there", expression, file, reason);
    }

    [Fact]
    public void AuditEntryDocumentation_ShouldListExactlyTheAcceptedCategories_WhenEveryMigrationIsApplied()
    {
        var documented = AuditCategoryScanner.DocumentedCategories(File.ReadAllText(Path.Combine(Root, AuditEntryFile)));

        documented.Should().BeEquivalentTo(Accepted(), "AuditEntry.Category documents the values the constraint accepts");
    }

    [Fact]
    public void LiveDbTest_ShouldWriteEveryCategoryTheCodeWrites_WhenListingThem()
    {
        var listed = AuditCategoryScanner.StringArrayField(File.ReadAllText(Path.Combine(Root, LiveDbTestFile)), "CategoriesTheCodeWrites");
        var written = Writes().Where(w => w.Value is not null).Select(w => w.Value!).ToHashSet(StringComparer.Ordinal);

        listed.Should().BeEquivalentTo(written,
            "the live-database test writes each category the code writes, so its list follows the code");
    }

    [Fact]
    public void AcceptedCategories_ShouldTakeTheLastDefinitionAndIgnoreComments_WhenMigrationsRedefineTheCheck()
    {
        var accepted = AuditCategoryScanner.AcceptedCategories(
        [
            ("002_b.sql", "ALTER TABLE audit_entries ADD CONSTRAINT audit_entries_category_check CHECK (category IN ('a', 'b'));\n" +
                          "-- rollback: CONSTRAINT audit_entries_category_check CHECK (category IN ('old'))"),
            ("001_a.sql", "CREATE TABLE audit_entries (CONSTRAINT audit_entries_category_check CHECK (category IN ('a')));"),
            ("003_c.sql", "/* audit_entries_category_check CHECK (category IN ('x')) */ SELECT 1;"),
        ]);

        accepted.Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void AcceptedCategories_ShouldThrow_WhenAMigrationNamesTheCheckInAFormItCannotRead()
    {
        var act = () => AuditCategoryScanner.AcceptedCategories(
        [
            ("001_a.sql", "CREATE TABLE audit_entries (CONSTRAINT audit_entries_category_check CHECK (category IN ('a')));"),
            ("002_b.sql", "ALTER TABLE audit_entries DROP CONSTRAINT audit_entries_category_check;"),
        ]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*002_b.sql*");
    }

    [Fact]
    public void CategoryWrites_ShouldReportEachForm_WhenReadingSource()
    {
        const string source = """
            class C
            {
                private const string Cat = "contact";
                void M(bool b, IAuditService audit, string x)
                {
                    audit.RecordAsync(t, category: "csat", action: "a");
                    audit.RecordAsync(t, "users", "a");
                    audit.RecordAsync(t, category: b ? "auth" : "rbac");
                    audit.RecordAsync(t, category: Infer(x));
                    audit.RecordAsync(t, category: x);
                    meter.RecordAsync(t, x.Value, 1);
                    var e = new AuditEntry { Category = Cat };
                    var f = new Other { Category = "ignored" };
                }
                static string Infer(string a) => a switch { "l" => "login_cat", _ => "config" };
            }
            """;

        var writes = AuditCategoryScanner.CategoryWrites(source, "C.cs").Select(w => w.Value).ToList();

        writes.Should().Equal("csat", "users", "auth", "rbac", "login_cat", "config", null, "contact");
    }
}
