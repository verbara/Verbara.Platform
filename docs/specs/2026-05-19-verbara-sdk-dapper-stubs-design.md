# `Verbara.Sdk.Dapper.Stubs` — AOT-clean drop-in replacement for `Dapper.dll`

**Status:** Approved 2026-05-19 (post-brainstorm) · **Owner:** Maintainer · **Target ship:** sub-deliverable D.1 of [ADR-0022 Phase D](../plans/completed/2026-05-19-phase-d-dapper-aot.md) · **Resolves upstream:** [DapperLib/DapperAOT#168](https://github.com/DapperLib/DapperAOT/issues/168) (filed by maintainer 2026-03-16, 0 comments since) · **Lives in:** Verbara.Sdk repo (`src/Verbara.Sdk.Dapper.Stubs/`)

## 1. Problem statement

`Dapper.AOT` 1.0.52 source-generator interceptors successfully replace consumer call sites at compile time so the real `Dapper.dll` method bodies are never executed at runtime. However, `Dapper.dll` remains in the publish output and `ilc` (the .NET Native AOT compiler) scans it during `dotnet publish -p:PublishAot=true`. The 45+ `DynamicMethod` + `MakeGenericType` + `Type.GetProperties()` / `GetConstructors()` usages inside `Dapper.dll` trigger ~50 fatal `IL3050` / `IL207x` diagnostics regardless of how many consumer call sites adopt the source generator.

This is the meta-blocker documented in issue #168 (also empirically reconfirmed in the [2026-05-19 baseline log](../operations/phase-d-validation/2026-05-19-baseline-aot-publish.log)).

Empirical inventory cross-repo (per [Day 1 findings](../operations/phase-d-validation/2026-05-19-day-1-findings.md)):

| Surface | Count |
|---|---|
| Total Dapper call sites (SDK + Pro + Platform storage) | ~447 |
| Sites in simple shape Dapper.AOT can intercept AS-IS | ~411 (92%) |
| Sites needing manual rewrite (R9 ∪ R10 ∪ R11) | ~32 in ~14 files |
| AOT diagnostics emitted today, 100% from `Dapper.dll` internals | 50 |

## 2. Solution

Ship a NuGet package `Verbara.Sdk.Dapper.Stubs` whose assembly is named `Dapper.dll` and mirrors the real Dapper 2.1.72 public API surface 1:1, but with:

- **Stub bodies** (`throw new NotSupportedException(...)` + `[RequiresDynamicCode]` + `[RequiresUnreferencedCode]`) on every method that's normally REPLACED by Dapper.AOT interceptors at runtime
- **Working implementations** on the small set of types that the generated interceptor code or the surrounding consumer code actually touches at runtime: `CommandDefinition` (struct ctor + property getters), `DynamicParameters` (class ctor + `.Add` + getters), `CommandFlags` (enum), `SqlMapper.ICustomQueryParameter` (interface definition for virtual dispatch)
- **AOT-safe by construction**: `<IsAotCompatible>true</IsAotCompatible>` + all AOT analyzers ON; no `Reflection.Emit`, no `MakeGenericType`, no untrimmed `GetProperties`

Drop-in replacement at the **assembly identity** level: storage packages replace `<PackageReference Include="Dapper" />` with `<PackageReference Include="Verbara.Sdk.Dapper.Stubs" />`. Compile-time `using Dapper;` + `SqlMapper.QueryAsync<T>` references resolve against our stub `Dapper.dll` (same name, same version, same public API) identically. Dapper.AOT analyzer sees our stub Dapper.dll and emits interceptors unchanged. The 9 storage packages need no source code changes other than the csproj swap + the new `[module: DapperAot]` AssemblyInfo.

### Why drop-in (not parallel package)

Considered alternative: `<PackageReference Include="Dapper" ExcludeAssets="runtime" />` + `<PackageReference Include="Verbara.Sdk.Dapper.Stubs" />` side-by-side. **Rejected** because:
- Both define `Dapper.SqlMapper` → compile-time `CS0433` (ambiguous type)
- Type identity must match for runtime binding to succeed (assembly name + version)
- Drop-in is exactly what issue #168 proposed and aligns with how upstream contribution would land

Verified empirically (Day 1 grep): no transitive dependency of the 9 storage packages brings Dapper in; all 9 reference it directly. Removing the direct reference + replacing with Stubs eliminates Dapper.dll from the runtime closure.

## 3. Architecture

### 3.1 Project structure

```
Verbara.Sdk/
├── src/Verbara.Sdk.Dapper.Stubs/
│   ├── Verbara.Sdk.Dapper.Stubs.csproj
│   ├── AssemblyInfo.cs
│   ├── README.md                                 ← drop-in nature + #168 reference
│   ├── Dapper/                                    ← namespace folder (matches namespace Dapper;)
│   │   ├── CommandDefinition.cs                   ← struct (WORKING — ctor + property getters)
│   │   ├── CommandFlags.cs                        ← enum (WORKING — semantic constants)
│   │   ├── DynamicParameters.cs                   ← class (PARTIAL — parameterless ctor + Add + Get work; template-ctor overload throws per DAP015 enforcement)
│   │   ├── CustomPropertyTypeMap.cs               ← class (STUB — all methods throw)
│   │   ├── DbString.cs                            ← class (STUB)
│   │   ├── DefaultTypeMap.cs                      ← class (STUB)
│   │   ├── ExplicitConstructorAttribute.cs        ← attribute (WORKING — Attribute base)
│   │   ├── FeatureSupport.cs                      ← class (STUB)
│   │   ├── IWrappedDataReader.cs                  ← interface (DEF ONLY)
│   │   ├── SimpleMemberMap.cs                     ← class (STUB)
│   │   ├── SqlDataRecordListTVPParameter.cs       ← class<T> (STUB)
│   │   ├── TableValuedParameter.cs                ← class (STUB)
│   │   └── SqlMapper/                             ← partial files for the big static class
│   │       ├── SqlMapper.cs                       ← partial class header + global state stubs
│   │       ├── SqlMapper.Execute.cs               ← Execute + ExecuteAsync overloads
│   │       ├── SqlMapper.ExecuteScalar.cs         ← ExecuteScalar<T> + Async overloads
│   │       ├── SqlMapper.Query.cs                 ← Query<T> + multi-mapping overloads
│   │       ├── SqlMapper.QueryAsync.cs            ← QueryAsync<T> + variants
│   │       ├── SqlMapper.QueryFirst.cs            ← QueryFirst[OrDefault] sync + async
│   │       ├── SqlMapper.QuerySingle.cs           ← QuerySingle[OrDefault] sync + async
│   │       ├── SqlMapper.QueryMultiple.cs         ← QueryMultiple + Async (returns GridReader)
│   │       ├── SqlMapper.TypeHandling.cs          ← AddTypeHandler + AddTypeMap + AsTableValuedParameter
│   │       ├── SqlMapper.Nested.GridReader.cs     ← nested class (STUB)
│   │       ├── SqlMapper.Nested.ICustomQueryParameter.cs ← interface (DEF ONLY — virtual dispatch target)
│   │       ├── SqlMapper.Nested.Identity.cs       ← nested class (STUB)
│   │       ├── SqlMapper.Nested.IDynamicParameters.cs ← interface
│   │       ├── SqlMapper.Nested.IMemberMap.cs     ← interface
│   │       ├── SqlMapper.Nested.IParameterCallbacks.cs ← interface
│   │       ├── SqlMapper.Nested.IParameterLookup.cs ← interface
│   │       ├── SqlMapper.Nested.ITypeHandler.cs   ← interface (throws if called)
│   │       ├── SqlMapper.Nested.ITypeMap.cs       ← interface
│   │       ├── SqlMapper.Nested.TypeHandler.cs    ← TypeHandler<T> abstract base (STUB)
│   │       ├── SqlMapper.Nested.StringTypeHandler.cs ← StringTypeHandler<T> (STUB)
│   │       ├── SqlMapper.Nested.UdtTypeHandler.cs ← (STUB)
│   │       ├── SqlMapper.Nested.Settings.cs       ← settings static class (no-op WORKING)
│   │       ├── SqlMapper.Nested.LiteralToken.cs   ← (STUB)
│   │       ├── SqlMapper.Nested.Link.cs           ← Link<T1,T2> (STUB)
│   │       ├── SqlMapper.Nested.TypeHandlerCache.cs ← TypeHandlerCache<T> (STUB)
│   │       └── SqlMapper.Nested.DontMap.cs        ← attribute (WORKING — Attribute base)
└── Tests/Verbara.Sdk.Dapper.Stubs.Tests/
    ├── Verbara.Sdk.Dapper.Stubs.Tests.csproj
    ├── SqlMapperStubTests.cs           ← every stub method throws expected exception
    ├── CommandDefinitionTests.cs       ← working impl roundtrips
    ├── DynamicParametersTests.cs       ← working impl roundtrips
    ├── AotAnnotationsTests.cs          ← reflection check: every throwing method has [RequiresDynamicCode] + [RequiresUnreferencedCode]
    └── PublicApiSurfaceTests.cs        ← reflection-based mirror check vs real Dapper 2.1.72
```

### 3.2 `Verbara.Sdk.Dapper.Stubs.csproj` (key settings)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- DROP-IN: assembly inside the package is literally named "Dapper" -->
    <AssemblyName>Dapper</AssemblyName>
    <RootNamespace>Dapper</RootNamespace>
    <!-- Assembly version matches real Dapper 2.1.72 for ref binding compatibility -->
    <AssemblyVersion>2.1.72.0</AssemblyVersion>
    <FileVersion>2.1.72.0</FileVersion>
    <!-- Package version distinguishes ours from real Dapper -->
    <PackageId>Verbara.Sdk.Dapper.Stubs</PackageId>
    <Version>2.1.72-aotstub.1</Version>
    <Description>AOT-clean drop-in replacement for Dapper.dll. Mirrors Dapper 2.1.72 public API surface so consumer code compiles and Dapper.AOT analyzer can intercept call sites. Runtime method bodies throw NotSupportedException with [RequiresDynamicCode] + [RequiresUnreferencedCode] annotations so ILC trims them cleanly during Native AOT publish. Use WITH the Dapper.AOT package. Resolves DapperLib/DapperAOT#168.</Description>
    <PackageTags>dapper;aot;native-aot;stubs;sdk</PackageTags>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <!-- AOT enforcement on the stubs themselves -->
    <IsAotCompatible>true</IsAotCompatible>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
    <EnableAotAnalyzer>true</EnableAotAnalyzer>
    <!-- New package (no published baseline); skip binary compat check -->
    <EnablePackageValidation>false</EnablePackageValidation>
    <!-- License: MIT (matches Verbara.Sdk repo) -->
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Verbara.Sdk.Dapper.Stubs.Tests" />
  </ItemGroup>
  <!-- ZERO PackageReferences: stubs depend only on BCL. No transitive Dapper. No Npgsql. -->
</Project>
```

## 4. Stub body template

Canonical pattern for methods normally replaced by Dapper.AOT interceptors:

```csharp
namespace Dapper;

public static partial class SqlMapper
{
    [RequiresDynamicCode("Real Dapper builds parameter emitters at runtime via DynamicMethod. " +
                         "This stub assumes Dapper.AOT interceptors replace the call site. " +
                         "If this body executes, Dapper.AOT did not intercept — verify [module: DapperAot] " +
                         "is applied and InterceptorsPreviewNamespaces MSBuild property includes Dapper.AOT.")]
    [RequiresUnreferencedCode("Real Dapper reflects over row type properties + constructors. " +
                              "Dapper.AOT interceptors replace this with statically-generated RowFactory<T>.")]
    public static IEnumerable<T> Query<T>(this IDbConnection cnn, string sql,
        object? param = null, IDbTransaction? transaction = null, bool buffered = true,
        int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(
            "Dapper.SqlMapper.Query<T> stub — Dapper.AOT did not intercept this call site. " +
            "See: https://aot.dapperlib.dev/gettingstarted");
}
```

For async methods that return `Task<T>`:

```csharp
public static Task<IEnumerable<T>> QueryAsync<T>(...)
    => Task.FromException<IEnumerable<T>>(
        new NotSupportedException("Dapper.SqlMapper.QueryAsync<T> stub — see https://aot.dapperlib.dev/gettingstarted"));
```

## 5. Working impls (the runtime-touched minority)

### 5.1 `Dapper.CommandDefinition`

```csharp
namespace Dapper;

public readonly struct CommandDefinition
{
    public CommandDefinition(string commandText, object? parameters = null,
        IDbTransaction? transaction = null, int? commandTimeout = null,
        CommandType? commandType = null, CommandFlags flags = CommandFlags.Buffered,
        CancellationToken cancellationToken = default)
    {
        CommandText = commandText;
        Parameters = parameters;
        Transaction = transaction;
        CommandTimeout = commandTimeout;
        CommandType = commandType;
        Flags = flags;
        CancellationToken = cancellationToken;
    }

    public string CommandText { get; }
    public object? Parameters { get; }
    public IDbTransaction? Transaction { get; }
    public int? CommandTimeout { get; }
    public CommandType? CommandType { get; }
    public CommandFlags Flags { get; }
    public CancellationToken CancellationToken { get; }
    public bool Buffered => (Flags & CommandFlags.Buffered) != 0;
}
```

No throw. Interceptor reads its properties to construct the real query.

### 5.2 `Dapper.DynamicParameters`

```csharp
namespace Dapper;

public sealed class DynamicParameters : SqlMapper.IDynamicParameters
{
    private readonly Dictionary<string, ParameterEntry> _parameters = new(StringComparer.Ordinal);

    public DynamicParameters() { }
    public DynamicParameters(object? template)
    {
        if (template is null) return;
        // For Dapper.AOT migration, DynamicParameters with a template is discouraged (DAP015).
        // Storage packages that use this pattern should be rewritten per the D.3 special-handling decision matrix.
        // Stub behavior: throw — forces explicit migration.
        throw new NotSupportedException(
            "Dapper.DynamicParameters(object template) stub — rewrite call site to use anonymous types " +
            "or NpgsqlCommand raw per Phase D.3 decision matrix. See DAP015 + day-1-findings.");
    }

    public void Add(string name, object? value = null, DbType? dbType = null,
        ParameterDirection? direction = null, int? size = null, byte? precision = null,
        byte? scale = null)
        => _parameters[name] = new ParameterEntry(name, value, dbType, direction, size, precision, scale);

    public IEnumerable<string> ParameterNames => _parameters.Keys;

    public T? Get<T>(string name) => (T?)_parameters[name].Value;

    // SqlMapper.IDynamicParameters.AddParameters — called by vanilla Dapper, NOT by Dapper.AOT interceptors.
    // If this fires, the consumer call site was not intercepted.
    [RequiresDynamicCode("Vanilla Dapper invokes IDynamicParameters.AddParameters via reflection-built " +
                         "parameter setters. Dapper.AOT interceptors replace this entirely.")]
    [RequiresUnreferencedCode("Vanilla Dapper reflects over the parameter values to build the IDbCommand " +
                              "parameter collection. Dapper.AOT interceptors handle this statically.")]
    void SqlMapper.IDynamicParameters.AddParameters(IDbCommand command, SqlMapper.Identity identity)
        => throw new NotSupportedException(
            "Dapper.DynamicParameters.AddParameters stub — Dapper.AOT did not intercept the parent call site. " +
            "Verify [module: DapperAot] is applied and InterceptorsPreviewNamespaces includes Dapper.AOT.");

    private readonly struct ParameterEntry
    {
        public ParameterEntry(string name, object? value, DbType? dbType,
            ParameterDirection? direction, int? size, byte? precision, byte? scale)
        {
            Name = name; Value = value; DbType = dbType; Direction = direction;
            Size = size; Precision = precision; Scale = scale;
        }
        public string Name { get; }
        public object? Value { get; }
        public DbType? DbType { get; }
        public ParameterDirection? Direction { get; }
        public int? Size { get; }
        public byte? Precision { get; }
        public byte? Scale { get; }
    }
}
```

The constructor + `.Add` + `.Get` + `.ParameterNames` are working. The `IDynamicParameters.AddParameters` (explicit interface impl) throws — only invoked when vanilla Dapper would have called it, which means Dapper.AOT didn't intercept.

### 5.3 `Dapper.SqlMapper.ICustomQueryParameter`

```csharp
namespace Dapper;

public static partial class SqlMapper
{
    public interface ICustomQueryParameter
    {
        void AddParameter(IDbCommand command, string name);
    }
}
```

Pure interface definition. The consumer (e.g. `PostgresSessionStore.JsonbParameter`) implements it. Interceptor-generated code detects this interface via standard virtual dispatch at runtime — AOT-clean.

### 5.4 `Dapper.CommandFlags`

```csharp
namespace Dapper;

[Flags]
public enum CommandFlags
{
    None = 0,
    Buffered = 1,
    Pipelined = 2,
    NoCache = 4,
}
```

### 5.5 Other working impls

- `Dapper.ExplicitConstructorAttribute` — `Attribute` subclass, no body
- `Dapper.SqlMapper.DontMap` — `Attribute` subclass, no body
- `Dapper.SqlMapper.Settings` — static class with idempotent property setters (no-op semantics, AOT-clean)

## 6. Multi-targeting + assembly identity

Real Dapper 2.1.72 ships TFMs `net48 / netstandard2.0 / netcoreapp3.1 / net6.0 / net8.0`. We ship `net8.0;net10.0`:
- `net8.0`: minimum TFM for stable C# interceptors; also Dapper.AOT analyzer's target
- `net10.0`: Verbara universe target

`<AssemblyVersion>2.1.72.0</AssemblyVersion>` matches real Dapper — any downstream `[assembly: ReferenceAssembly]` reference to `Dapper, Version=2.1.72.0` is satisfied.

`<AssemblyName>Dapper</AssemblyName>` → output is `Dapper.dll`. NuGet pack embeds at `lib/net8.0/Dapper.dll` + `lib/net10.0/Dapper.dll`.

## 7. Tests

`Verbara.Sdk.Dapper.Stubs.Tests` project (xUnit 2.9.3 + FluentAssertions 7.x, matching SDK convention):

1. **`SqlMapperStubTests`** (~30 tests): every public method in stubs `SqlMapper` (and its nested types) throws `NotSupportedException`. Sync methods throw directly; async return `Task.FromException`.
2. **`CommandDefinitionTests`**: constructor + property getters round-trip correctly. Buffered flag computes from Flags. No throws.
3. **`DynamicParametersTests`**: `.Add(name, value)` then `.Get<T>(name)` round-trips. `ParameterNames` enumerates correctly. DbType / Direction / Size preserved.
4. **`AotAnnotationsTests`** (the safety gate): for every public method in stubs that's documented as throwing, use reflection to verify both `[RequiresDynamicCode]` AND `[RequiresUnreferencedCode]` are present. Prevents regression where a developer adds a stub method without annotations.
5. **`PublicApiSurfaceTests`** (the compat gate): load real Dapper 2.1.72 via `Assembly.LoadFrom("~/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.dll")`, enumerate all `public` types/methods/properties/events, compare against stubs assembly. **Test fails if stubs are missing a member or contain an extra member.** Guarantees drop-in compatibility.

## 8. Packaging + consumer adoption

### 8.1 Pack to local feeds

```sh
cd /media/Data/Source/Verbara/Verbara.Sdk
dotnet pack src/Verbara.Sdk.Dapper.Stubs/ -c Release \
    -o /media/Data/Source/Verbara/local-nuget-feed/
# Sync to Platform repo copy per feedback_nuget_two_feeds.md:
cp /media/Data/Source/Verbara/local-nuget-feed/Verbara.Sdk.Dapper.Stubs.2.1.72-aotstub.1.nupkg \
   /media/Data/Source/Verbara/Verbara.Platform/local-nuget-feed/
# Clear NuGet cache so consumers pull fresh:
rm -rf ~/.nuget/packages/verbara.sdk.dapper.stubs/
```

### 8.2 Per-storage-package csproj diff (9 .csproj cross-repo)

```diff
  <ItemGroup>
    <PackageReference Include="Npgsql" />
-   <PackageReference Include="Dapper" />
+   <PackageReference Include="Verbara.Sdk.Dapper.Stubs" />
+   <PackageReference Include="Dapper.AOT" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
  </ItemGroup>
+ <PropertyGroup>
+   <InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);Dapper.AOT</InterceptorsPreviewNamespaces>
+ </PropertyGroup>
```

Plus new `AssemblyInfo.cs` with `[module: DapperAot]`.

### 8.3 Cross-repo `Directory.Packages.props` (× 3 repos)

```diff
- <PackageVersion Include="Dapper" Version="2.1.72" />
+ <PackageVersion Include="Verbara.Sdk.Dapper.Stubs" Version="2.1.72-aotstub.N" />
+ <PackageVersion Include="Dapper.AOT" Version="1.0.52" />
```

## 9. Verification gates

Stubs project itself (D.1 gates):

1. **G-stubs-1: stubs build clean** — `dotnet build -c Release` → 0 warnings, 0 errors under `TreatWarningsAsErrors=true` + all AOT analyzers ON
2. **G-stubs-2: stubs tests verde** — 5 test classes, all green
3. **G-stubs-3: `PublicApiSurfaceTests` confirms 1:1 mirror** vs real Dapper 2.1.72
4. **G-stubs-4: dogfood canary** — pack stubs → restore in `Verbara.Sdk.Sessions.Postgres` → build + tests verde + `Verbara.Sdk.Sessions.Postgres.Tests` (Testcontainers Postgres) pass
5. **G-stubs-5: AOT publish smoke on Platform.Api** — with Sessions.Postgres using stubs + Dapper.AOT interceptors → diagnostic count drops by the # attributable to Sessions surface (verifies stubs design works end-to-end before sweep)

Full Phase D triple gate (after D.2 + D.3 sweep) still applies per [the amended Phase D plan](../plans/completed/2026-05-19-phase-d-dapper-aot.md).

## 10. Risks

| # | Risk | P | Mitigation |
|---|---|---|---|
| S1 | Stubs miss a public member used by some transitive package or Dapper.AOT-generated code → runtime `MissingMethodException` | M | `PublicApiSurfaceTests` blocks this at build time; staged validation in G-stubs-4 catches what slips through |
| S2 | Stubs include an extra public member not in real Dapper → consumer code compiles against our stubs but breaks if user reverts to real Dapper | L | `PublicApiSurfaceTests` blocks extras too |
| S3 | Drop-in assembly identity collision: Dapper transitively pulled in by some other package → both `Dapper.dll`s in publish output | L | Verified empirically (Day 1 grep): no transitive Dapper in our closure. If a future package adds one, NuGet warns about conflicting assemblies — tracked in CI |
| S4 | Working impls of `DynamicParameters` / `CommandDefinition` diverge from real Dapper semantics in subtle ways | M | Tests focused on the round-trip semantics that interceptors actually depend on; vanilla-Dapper-compat is NOT a goal (since the interceptor is supposed to win) |
| S5 | `[RequiresDynamicCode]` + `[RequiresUnreferencedCode]` annotations have wrong syntax / missing `Url` property / wrong message — ILC may not trim cleanly | M | `AotAnnotationsTests` verifies presence + non-empty message; G-stubs-5 verifies ILC behaves correctly |
| S6 | Dapper.AOT analyzer behaves differently when seeing our stubs vs real Dapper (e.g., type identity hash mismatch) | L | Our stubs match `[AssemblyVersion]` + namespaces + type names + member signatures — analyzer cannot tell apart at the IL level |
| S7 | Future Dapper public API changes (e.g., 2.2.x release) — our stubs go stale | L | Pinned to 2.1.72 indefinitely; future Dapper upgrades require stubs refresh (small mechanical work since API mirror test catches drift) |
| S8 | Upstream contribution to dapperlib/dapperaot rejected by Marc Gravell — stubs remain our own forever | L | Acceptable; package stays in Verbara.Sdk repo, MIT-licensed, community-usable |

## 11. Upstream contribution (post-D.4 triple gate)

Sequence:
1. Fork `dapperlib/dapperaot`
2. Add `src/Dapper.AOT.Stubs/` with the stubs code (rebrand namespace/package to `Dapper.AOT.Stubs`)
3. Update README + close issue #168 with resolution + PR link
4. Submit PR
5. **Parallel**: our `Verbara.Sdk.Dapper.Stubs` keeps working independent. If upstream merge → migrate consumer csprojs to `Dapper.AOT.Stubs` from upstream; if not (Marc has not engaged with #168 in 2 months) → keep our package indefinitely.

## 12. Out of scope (deferred)

- Dapper 2.2.x or later public API mirroring (only when/if we upgrade past 2.1.72)
- Mirroring `Dapper.Contrib` or other Dapper companion packages (we don't use them)
- `Dapper.AOT` analyzer bug fixes (R11 — file separately, 10-line generator change)
- Dynamic-SQL support per upstream #157 (we rewrite the 14 DynamicParameters sites to anonymous types or NpgsqlCommand raw per D.3 instead)
- `Dapper.AOT` CommandDefinition support per upstream #153 (not needed once we adopt Stubs)

## 13. References

- [ADR-0022](../decisions/0022-platform-api-aot-shipping-path.md) (Amendment §7 unmasks Dapper; future Amendment §8 reports Phase D + Stubs outcome)
- [Phase D plan](../plans/completed/2026-05-19-phase-d-dapper-aot.md) — companion execution plan
- [Day 1 findings](../operations/phase-d-validation/2026-05-19-day-1-findings.md) — empirical inventory + R10/R11/R12 confirmation
- [Day 0 baseline](../operations/phase-d-validation/2026-05-19-baseline-aot-publish.log) — 50 diagnostics from `Dapper.dll`
- [DapperLib/DapperAOT#168](https://github.com/DapperLib/DapperAOT/issues/168) — maintainer's own prior proposal (this spec's parent design)
- [DapperLib/DapperAOT#153](https://github.com/DapperLib/DapperAOT/issues/153) — CommandDefinition interception PR (open + unmerged 12 months; obviated by Stubs)
- [Dapper 2.1.72 source](https://github.com/DapperLib/Dapper/tree/2.1.72) — reference for public API surface mirror
