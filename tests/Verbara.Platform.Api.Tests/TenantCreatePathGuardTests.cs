using System.Text.RegularExpressions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// realtime-objects-stay-in-their-tenant (design D1, Architectural Risk): the tenant-id rule is only as complete as
/// the list of paths that construct a tenant. Every <c>new Tenant</c> in <c>src/</c> is pinned here; the ones that take
/// a new id must create it through <c>ITenantIdRegistry.TryCreateAsync</c> (the setup host and the setup customer's
/// adoption of an exactly equal stored id stay upserts). A construction this list does not know turns the test red.
/// </summary>
public sealed partial class TenantCreatePathGuardTests
{
    /// <summary>(file, variable) of each construction that copies an existing tenant (an update, an adoption or a read).</summary>
    private static readonly string[] Copies =
    [
        "src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantStore.cs:return",
        "src/Verbara.Platform.Api/Endpoints/PartnerCustomerEndpoints.cs:updated",
        "src/Verbara.Platform.Api/Endpoints/ManagementTenantEndpoints.cs:updated",
        "src/Verbara.Platform.Api/Endpoints/TenantSettingsEndpoints.cs:updated",
        "src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs:adopted",
        "src/Verbara.Platform.Api/Endpoints/OnboardingEndpoints.cs:updated",
        "src/Verbara.Platform.Api/Endpoints/OnboardingEndpoints.cs:updated",
        "src/Verbara.Platform.Api/Endpoints/OnboardingEndpoints.cs:updated",
    ];

    /// <summary>(file, variable) of each construction that takes a new id.</summary>
    private static readonly string[] NewIds =
    [
        "src/Verbara.Platform.Api/Endpoints/PartnerCustomerEndpoints.cs:tenant",
        "src/Verbara.Platform.Api/Endpoints/ManagementTenantEndpoints.cs:tenant",
        "src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs:hostTenant",
        "src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs:customerTenant",
    ];

    [GeneratedRegex(@"(?:var\s+(?<v>\w+)\s*=|(?<v>return))\s+new\s+(?:Verbara\.Sdk\.Pro\.MultiTenant\.)?Tenant\s*(?:\{|\(\)|$)", RegexOptions.Multiline)]
    private static partial Regex Construction();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Verbara.Platform.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void TenantConstructions_ShouldBeTheKnownOnes_WhenTheSourceTreeIsScanned()
    {
        var root = RepoRoot();
        var found = Directory.EnumerateFiles(Path.Join(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Construction().Matches(File.ReadAllText(f))
                .Select(m => Path.GetRelativePath(root, f).Replace('\\', '/') + ":" + m.Groups["v"].Value))
            .ToList();

        found.Should().BeEquivalentTo([.. Copies, .. NewIds],
            "a new tenant construction must be reviewed: a new id goes through ITenantIdRegistry.TryCreateAsync");
    }

    [Theory]
    [InlineData("src/Verbara.Platform.Api/Endpoints/PartnerCustomerEndpoints.cs")]
    [InlineData("src/Verbara.Platform.Api/Endpoints/ManagementTenantEndpoints.cs")]
    [InlineData("src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs")]
    public void CreatePath_ShouldCreateThroughTheRegistry_WhenItTakesANewTenantId(string relativePath)
    {
        var text = File.ReadAllText(Path.Join([RepoRoot(), .. relativePath.Split('/')]));

        text.Should().Contain("TryCreateAsync(", $"{relativePath} creates a tenant with a new id");
    }
}
