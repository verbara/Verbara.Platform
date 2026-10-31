namespace Verbara.Platform.Governance.Tests;

/// <summary>
/// The Sdk marks its deprecated symbols with <c>VSDK</c> diagnostics, and with warnings as errors the build is what
/// proves Platform uses none of them. That proof only holds while nothing here silences those ids, so this scans the
/// tree for a suppression. The self-tests pin the walk and each detector, because a scan that reads nothing is green.
/// </summary>
public sealed class VsdkSuppressionGuardTests
{
    // Conservative floors: the tree held 76 project files when this was written. Well below that defeats the "walked
    // nothing" failure while tolerating churn.
    private const int MinimumProjectFiles = 50;
    private const int MinimumSourceFiles = 500;

    [Fact]
    public void Build_ShouldNotSuppressVsdkDiagnostics_WhenTheTreeIsScanned()
    {
        var root = TestTreeSource.RepoRoot();

        var suppressions = TestTreeSource.EnumerateBuildInputs(root)
            .SelectMany(file => VsdkSuppressionScanner.Scan(File.ReadAllText(file), Path.GetRelativePath(root, file)))
            .ToList();

        suppressions.Should().BeEmpty(
            "a suppressed VSDK diagnostic would let a deprecated Sdk symbol through the build:" + Environment.NewLine
            + string.Join(Environment.NewLine, suppressions));
    }

    [Fact]
    public void EnumerateBuildInputs_ShouldReachTheRealBuildFiles_WhenWalkingTheRepo()
    {
        var root = TestTreeSource.RepoRoot();

        var files = TestTreeSource.EnumerateBuildInputs(root).ToList();

        files.Should().Contain(Path.Join(root, "Directory.Build.props"));
        files.Count(f => f.EndsWith(".csproj", StringComparison.Ordinal)).Should().BeGreaterThan(MinimumProjectFiles);
        files.Count(f => f.EndsWith(".cs", StringComparison.Ordinal)).Should().BeGreaterThan(MinimumSourceFiles);
    }

    [Fact]
    public void EnumerateBuildInputs_ShouldFindDotfilesAndSkipBuildOutput_WhenWalkingATree()
    {
        var root = Directory.CreateTempSubdirectory("vsdk-guard-").FullName;
        try
        {
            string[] read =
            [
                ".editorconfig",
                ".globalconfig",
                Path.Join("src", ".editorconfig"),
                Path.Join("src", "App", "App.csproj"),
                Path.Join("src", "App", "Program.cs"),
                Path.Join("src", "Directory.Build.targets"),
            ];
            string[] skipped =
            [
                Path.Join("src", "App", "bin", "Skipped.csproj"),
                Path.Join("src", "App", "obj", "Skipped.cs"),
                Path.Join(".worktrees", "other", "Skipped.csproj"),
                Path.Join("web", "node_modules", "Skipped.props"),
                Path.Join("src", "App", "notes.md"),
            ];
            foreach (var path in read.Concat(skipped).Select(file => Path.Join(root, file)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }

            var found = TestTreeSource.EnumerateBuildInputs(root).Select(f => Path.GetRelativePath(root, f));

            found.Should().BeEquivalentTo(read);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><NoWarn>$(NoWarn);VSDK0003</NoWarn></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><NoWarn>\n  CS1591;\n  VSDK0003\n</NoWarn></PropertyGroup></Project>")]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"Verbara.Sdk\" NoWarn=\"VSDK0003\" /></ItemGroup></Project>")]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"Verbara.Sdk\"><NoWarn>VSDK0003</NoWarn></PackageReference></ItemGroup></Project>")]
    public void Scan_ShouldFlag_WhenAnMsBuildNoWarnNamesAVsdkId(string project)
    {
        VsdkSuppressionScanner.Scan(project, "App.csproj").Should().ContainSingle().Which.File.Should().Be("App.csproj");
    }

    [Theory]
    [InlineData("#pragma warning disable VSDK0003")]
    [InlineData("    #pragma warning disable CS0618, VSDK0002")]
    [InlineData("# pragma warning disable vsdk0006 // lower case")]
    public void Scan_ShouldFlag_WhenAPragmaNamesAVsdkId(string line)
    {
        VsdkSuppressionScanner.Scan("class C { }\n" + line + "\n", "C.cs").Should().ContainSingle().Which.Line.Should().Be(2);
    }

    [Theory]
    [InlineData(".editorconfig")]
    [InlineData(".globalconfig")]
    public void Scan_ShouldFlag_WhenAnAnalyzerConfigNamesAVsdkId(string file)
    {
        const string config = "[*.cs]\ndotnet_diagnostic.VSDK0003.severity = none\n";

        VsdkSuppressionScanner.Scan(config, file).Should().ContainSingle().Which.Line.Should().Be(2);
    }

    [Theory]
    [InlineData("App.csproj", "<Project><!-- <NoWarn>VSDK0003</NoWarn> --></Project>")]
    [InlineData("App.csproj", "<Project><PropertyGroup><NoWarn>CS1591;NU1902</NoWarn></PropertyGroup></Project>")]
    [InlineData("App.csproj", "<Project><PropertyGroup><Description>VSDK0003</Description></PropertyGroup></Project>")]
    [InlineData("C.cs", "#pragma warning disable CA2012 // not VSDK0003")]
    [InlineData("C.cs", "const string Id = \"VSDK0003\";")]
    [InlineData(".editorconfig", "# dotnet_diagnostic.VSDK0003.severity = none")]
    [InlineData(".editorconfig", "dotnet_diagnostic.CA1000.severity = none")]
    public void Scan_ShouldNotFlag_WhenNothingIsSuppressed(string file, string source)
    {
        VsdkSuppressionScanner.Scan(source, file).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldThrow_WhenAProjectFileDoesNotParse()
    {
        var act = () => VsdkSuppressionScanner.Scan("<Project>", "App.csproj").ToList();

        act.Should().Throw<System.Xml.XmlException>("a file the guard cannot read must not count as clean");
    }
}
