using System.Text.RegularExpressions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// realtime-objects-stay-in-their-tenant (design D1, Architectural Risk): the tenant-id rule is only as complete as
/// the list of paths that construct a tenant. Every tenant construction in <c>src/</c>, in any form, and every raw
/// <c>INSERT INTO tenants</c> is pinned here; the constructions that take
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

    /// <summary>The files that may write a raw <c>INSERT INTO tenants</c>: the registry's create and the store's upsert.</summary>
    private static readonly string[] RawInserts =
    [
        "src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantIdRegistry.cs",
        "src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantStore.cs",
    ];

    /// <summary>
    /// Every textual form that constructs a tenant: <c>new Tenant {…}</c> / <c>new Tenant(…)</c> / a bare
    /// <c>new Tenant</c> at the end of a line, anywhere (assignment, return, call argument, initializer), with or
    /// without the namespace, and a target-typed <c>new()</c> whose declared type is <c>Tenant</c>.
    /// </summary>
    [GeneratedRegex(@"\bnew\s+(?:global::)?(?:Verbara\.Sdk\.Pro\.MultiTenant\.)?Tenant(?![\w.<])|\bTenant\??\s+(?<tv>\w+)\s*(?:\([^()]*\)\s*)?(?:=|=>)\s*new\s*[({]", RegexOptions.Multiline)]
    private static partial Regex Construction();

    /// <summary>A raw insert into <c>tenants</c>, quoted or schema-qualified, as it appears in C# source text.</summary>
    [GeneratedRegex(@"INSERT\s+INTO\s+(?:\\?""?public\\?""?\.)?\\?""?tenants(?!\w)", RegexOptions.IgnoreCase)]
    private static partial Regex RawInsert();

    [GeneratedRegex(@"(?:var\s+(?<v>\w+)\s*=|(?<v>\w+)\s*=|(?<v>return))\s*$")]
    private static partial Regex AssignedTo();

    [GeneratedRegex(@"(?<v>\w+)\s*\([^()]*$")]
    private static partial Regex ArgumentOf();

    /// <summary>
    /// Names each construction in <paramref name="text"/> as <c>{file}:{variable}</c>, <c>{file}:return</c> or
    /// <c>{file}:arg:{method}</c>, by what precedes it on its line.
    /// </summary>
    internal static IEnumerable<string> Constructions(string file, string text) =>
        Construction().Matches(text).Select(m =>
        {
            if (m.Groups["tv"].Success)
                return $"{file}:{m.Groups["tv"].Value}";
            var lineStart = m.Index == 0 ? 0 : text.LastIndexOf('\n', m.Index - 1) + 1;
            var before = text[lineStart..m.Index].TrimEnd();
            var assigned = AssignedTo().Match(before);
            if (assigned.Success)
                return $"{file}:{assigned.Groups["v"].Value}";
            var argument = ArgumentOf().Match(before);
            return argument.Success ? $"{file}:arg:{argument.Groups["v"].Value}" : $"{file}:other";
        });

    private static IEnumerable<(string Relative, string Text)> SourceFiles()
    {
        var root = RepoRoot();
        return Directory.EnumerateFiles(Path.Join(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)));
    }

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
        var found = SourceFiles().SelectMany(f => Constructions(f.Relative, f.Text)).ToList();

        found.Should().BeEquivalentTo([.. Copies, .. NewIds],
            "a new tenant construction must be reviewed: a new id goes through ITenantIdRegistry.TryCreateAsync");
    }

    [Fact]
    public void RawTenantInserts_ShouldBeTheKnownOnes_WhenTheSourceTreeIsScanned()
    {
        var found = SourceFiles().Where(f => RawInsert().IsMatch(f.Text)).Select(f => f.Relative).ToList();

        found.Should().BeEquivalentTo(RawInserts,
            "a raw INSERT INTO tenants outside the registry and the store bypasses the tenant-id rule");
    }

    /// <summary>Mutation probes: each form a fifth create path could take is seen, so the tree scan turns red.</summary>
    [Theory]
    [InlineData("var t = new Tenant { TenantId = id };", "x.cs:t")]
    [InlineData("await store.UpsertAsync(new Tenant { TenantId = id }, ct);", "x.cs:arg:UpsertAsync")]
    [InlineData("await store.UpsertAsync(\n    new Tenant\n    {\n        TenantId = id,\n    });", "x.cs:other")]
    [InlineData("await store.SaveAsync(other, new Verbara.Sdk.Pro.MultiTenant.Tenant { TenantId = id });", "x.cs:arg:SaveAsync")]
    [InlineData("Tenant t = new() { TenantId = id };", "x.cs:t")]
    [InlineData("Tenant? t = new()\n{\n    TenantId = id,\n};", "x.cs:t")]
    [InlineData("_tenant = new Tenant(id, name);", "x.cs:_tenant")]
    [InlineData("return new global::Verbara.Sdk.Pro.MultiTenant.Tenant { TenantId = id };", "x.cs:return")]
    [InlineData("private static Tenant Make(string id) => new() { TenantId = id };", "x.cs:Make")]
    public void Constructions_ShouldNameTheConstruction_WhenItTakesAnyForm(string code, string expected)
    {
        Constructions("x.cs", code).Should().Equal(expected);
    }

    [Theory]
    [InlineData("var s = new TenantSettings { };")]
    [InlineData("var r = new TenantCreateResult(x);")]
    [InlineData("var l = new List<Tenant>();")]
    [InlineData("var t = new TenantId(id);")]
    public void Constructions_ShouldIgnoreOtherTypes_WhenTheNameOnlyStartsWithTenant(string code)
    {
        Constructions("x.cs", code).Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"INSERT INTO tenants (tenant_id) VALUES (@Id)\"")]
    [InlineData("\"insert into public.tenants (tenant_id) VALUES (@Id)\"")]
    [InlineData("\"INSERT INTO \\\"tenants\\\" (tenant_id) VALUES (@Id)\"")]
    [InlineData("\"INSERT\n INTO tenants (tenant_id)\"")]
    public void RawInsert_ShouldMatch_WhenTheSqlInsertsIntoTenants(string code)
    {
        RawInsert().IsMatch(code).Should().BeTrue();
    }

    [Fact]
    public void RawInsert_ShouldNotMatch_WhenTheSqlInsertsIntoAnotherTable()
    {
        RawInsert().IsMatch("INSERT INTO tenants_audit (id) VALUES (1)").Should().BeFalse();
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
