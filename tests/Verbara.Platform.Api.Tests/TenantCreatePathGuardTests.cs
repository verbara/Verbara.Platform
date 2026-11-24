using System.Text.RegularExpressions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// realtime-objects-stay-in-their-tenant (design D1, Architectural Risk): the tenant-id rule is only as complete as
/// the list of paths that construct a tenant. The tenant constructions in <c>src/</c> that the forms below detect, and
/// every raw <c>INSERT INTO tenants</c>, are pinned here; the constructions that take a new id must create it through
/// <c>ITenantIdRegistry.TryCreateAsync</c> (the setup host and the setup customer's adoption of an exactly equal stored
/// id stay upserts). A detected construction this list does not know turns the test red.
/// </summary>
/// <remarks>
/// <para>
/// The scan reads source text with regular expressions; it is not a C# parser. It detects:
/// <list type="bullet">
/// <item><description><c>new Tenant</c> (with or without <c>global::</c> and the namespace, any arguments or
/// initializer, in any position).</description></item>
/// <item><description>a target-typed <c>new()</c> whose declared type is written as <c>Tenant</c> on the same
/// declaration: <c>Tenant t = new()…</c>, <c>Tenant? t = new()…</c>, <c>Tenant Make(…) =&gt; new()…</c>.</description></item>
/// <item><description>a target-typed <c>new()</c> passed as an argument (positional or named) or returned
/// (<c>return new()…</c>) whose object initializer sets both <c>TenantId</c> and <c>Name</c> (the two required members
/// of <c>Tenant</c>) and does not set <c>TenantId</c> to <c>new TenantId(…)</c> (<c>Tenant.TenantId</c> is a string;
/// the types that take a <c>TenantId</c> value are other types).</description></item>
/// </list>
/// </para>
/// <para>
/// It does not detect: a target-typed <c>new()</c> anywhere else (a lambda body such as <c>x =&gt; new() {…}</c>, a
/// collection element, a property or field initializer whose type is not written as <c>Tenant</c>, a conditional
/// branch without an argument or return position, an assignment to a variable or member declared elsewhere); an
/// argument or returned <c>new()</c> whose initializer sets those members through a nested object or leaves the
/// required members to a <c>[SetsRequiredMembers]</c> constructor; an initializer whose braces are unbalanced inside a
/// string or comment; a <c>with</c> copy; a construction outside <c>src/</c> or in another assembly (Pro); and it does
/// not skip comments or strings, so a commented-out form still counts.
/// </para>
/// </remarks>
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
    /// <c>new Tenant {…}</c> / <c>new Tenant(…)</c> / a bare <c>new Tenant</c> at the end of a line, anywhere
    /// (assignment, return, call argument, initializer), with or without the namespace; and a target-typed
    /// <c>new()</c> whose declared type is written as <c>Tenant</c> on the same declaration (group <c>tv</c>).
    /// </summary>
    [GeneratedRegex(@"\bnew\s+(?:global::)?(?:Verbara\.Sdk\.Pro\.MultiTenant\.)?Tenant(?![\w.<])|\bTenant\??\s+(?<tv>\w+)\s*(?:\([^()]*\)\s*)?(?:=|=>)\s*new\s*[({]", RegexOptions.Multiline)]
    private static partial Regex Construction();

    /// <summary>
    /// A target-typed <c>new(…)</c> followed by an object initializer, in argument position (after <c>(</c>, <c>,</c> or
    /// a named argument's <c>name:</c>) or returned (<c>return</c>, group <c>ret</c>); the initializer is read by
    /// <see cref="InitializerAt"/>.
    /// </summary>
    [GeneratedRegex(@"(?:(?<ret>\breturn)|[(,]|\b\w+\s*:)\s*new\s*\([^()]*\)\s*(?=\{)")]
    private static partial Regex TargetTypedArgumentOrReturn();

    /// <summary>An initializer member <c>TenantId = …</c> whose value is not <c>new TenantId(…)</c>.</summary>
    [GeneratedRegex(@"[{,]\s*TenantId\s*=(?!=)(?!\s*new\s+(?:\w+\.)*TenantId\s*\()")]
    private static partial Regex StringTenantIdMember();

    /// <summary>An initializer member <c>Name = …</c>.</summary>
    [GeneratedRegex(@"[{,]\s*Name\s*=(?!=)")]
    private static partial Regex NameMember();

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
        }).Concat(TargetTypedArgumentOrReturn().Matches(text)
            .Where(m => InitializerAt(text, m.Index + m.Length) is { } body
                && StringTenantIdMember().IsMatch(body) && NameMember().IsMatch(body))
            .Select(m =>
            {
                if (m.Groups["ret"].Success)
                    return $"{file}:return";
                // The call is the innermost unclosed "name(" before the match, on this line or the ones above it.
                var argument = ArgumentOf().Match(text[Math.Max(0, m.Index - 400)..(m.Index + 1)]);
                return argument.Success ? $"{file}:arg:{argument.Groups["v"].Value}" : $"{file}:other";
            }));

    /// <summary>
    /// The object initializer that opens at <paramref name="open"/> (a <c>{</c>), up to its matching <c>}</c> by brace
    /// count (braces inside strings or comments are counted too), or <see langword="null"/> when it never closes.
    /// </summary>
    private static string? InitializerAt(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return text[open..(i + 1)];
        }
        return null;
    }

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
    [InlineData("await store.UpsertAsync(new() { TenantId = id, Name = id }, ct);", "x.cs:arg:UpsertAsync")]
    [InlineData("await store.SaveAsync(other, new()\n{\n    TenantId = id,\n    Name = name,\n});", "x.cs:arg:SaveAsync")]
    [InlineData("await registry.TryCreateAsync(\n    tenant: new() { Name = name, TenantId = id }, ct);", "x.cs:arg:TryCreateAsync")]
    [InlineData("return new()\n{\n    TenantId = request.TenantId,\n    Name = request.Name,\n};", "x.cs:return")]
    [InlineData("return new() { TenantId = id, Name = id, Options = new() { MaxActiveCampaigns = 1 } };", "x.cs:return")]
    public void Constructions_ShouldNameTheConstruction_WhenItTakesAnyForm(string code, string expected)
    {
        Constructions("x.cs", code).Should().Equal(expected);
    }

    [Theory]
    [InlineData("var s = new TenantSettings { };")]
    [InlineData("var r = new TenantCreateResult(x);")]
    [InlineData("var l = new List<Tenant>();")]
    [InlineData("var t = new TenantId(id);")]
    [InlineData("await store.SaveAsync(new() { TenantId = new TenantId(id), Name = name }, ct);")]
    [InlineData("return new() { TeamId = id, TenantId = new Verbara.Platform.Core.TenantId(t), Name = n };")]
    [InlineData("await store.SaveAsync(new() { TenantId = id, UserId = user }, ct);")]
    [InlineData("return new() { TenantId = id, DisplayName = name };")]
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
