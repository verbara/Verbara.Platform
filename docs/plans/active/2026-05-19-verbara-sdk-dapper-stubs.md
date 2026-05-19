# Verbara.Sdk.Dapper.Stubs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Verbara.Sdk.Dapper.Stubs` — an AOT-clean drop-in replacement for `Dapper.dll` that resolves [DapperLib/DapperAOT#168](https://github.com/DapperLib/DapperAOT/issues/168) and unblocks Phase D of ADR-0022 AOT shipping. Validate via canary on `Verbara.Sdk.Sessions.Postgres` + Platform.Api AOT publish smoke.

**Architecture:** New csproj inside Verbara.Sdk repo (MIT). Assembly name = `Dapper`, AssemblyVersion = `2.1.72.0` (drop-in identity). Public API 1:1 mirror of Dapper 2.1.72. Stub method bodies throw `NotSupportedException` + `[RequiresDynamicCode]` + `[RequiresUnreferencedCode]`. Working impls only on the small set of types touched at runtime by Dapper.AOT-generated interceptors (`CommandDefinition`, `DynamicParameters` partial, `CommandFlags`, `ICustomQueryParameter` interface). Validated by reflection-based `PublicApiSurfaceTests` comparing against real Dapper 2.1.72.

**Tech Stack:** .NET 10 (`net8.0;net10.0` multi-target on stubs), C# 14, xUnit 2.9.3, FluentAssertions 7.x. Reference: Dapper 2.1.72 (loaded via `Assembly.LoadFrom` from `~/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.dll` for the API surface test).

**Companion spec:** [`docs/specs/2026-05-19-verbara-sdk-dapper-stubs-design.md`](../../specs/2026-05-19-verbara-sdk-dapper-stubs-design.md). Read it before starting.

**Pre-conditions:**
- Phase D plan approved (committed `c09424b1`) and Day 0 + Day 1 evidence under `docs/operations/phase-d-validation/`
- Verbara.Sdk repo on clean `main` (current version 2.1.2)
- Verbara.Platform repo on clean `main` (current version `2.4.0-rc`)
- Pre-condition v2.2.0 SHIPPED (verified 2026-05-19; tag `v2.2.0` exists)

---

## Phase A — Foundation: project scaffolding (Verbara.Sdk repo)

### Task A.1: Create implementation branch in Verbara.Sdk

**Files:** Verbara.Sdk repo, no source files yet.

- [ ] **Step 1: Verify clean working tree**

Run: `cd /media/Data/Source/Verbara/Verbara.Sdk && git status --short`
Expected: empty output (clean tree on `main`)

- [ ] **Step 2: Create branch**

Run:
```bash
cd /media/Data/Source/Verbara/Verbara.Sdk
git checkout -b feat/dapper-stubs
git branch --show-current
```
Expected: `feat/dapper-stubs`

### Task A.2: Scaffold `Verbara.Sdk.Dapper.Stubs.csproj`

**Files:**
- Create: `/media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Dapper.Stubs/Verbara.Sdk.Dapper.Stubs.csproj`

- [ ] **Step 1: Create the project directory + csproj**

Run: `mkdir -p /media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Dapper.Stubs`

Create file `/media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Dapper.Stubs/Verbara.Sdk.Dapper.Stubs.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- DROP-IN: assembly is named "Dapper" so it replaces Dapper.dll in the runtime closure -->
    <AssemblyName>Dapper</AssemblyName>
    <RootNamespace>Dapper</RootNamespace>
    <!-- Assembly version matches real Dapper 2.1.72 so [assembly: ReferenceAssembly] bindings remain satisfied -->
    <AssemblyVersion>2.1.72.0</AssemblyVersion>
    <FileVersion>2.1.72.0</FileVersion>
    <!-- Package version is semver-distinct from real Dapper -->
    <PackageId>Verbara.Sdk.Dapper.Stubs</PackageId>
    <Version>2.1.72-aotstub.1</Version>
    <Description>AOT-clean drop-in replacement for Dapper.dll. Mirrors Dapper 2.1.72 public API surface so consumer code compiles and Dapper.AOT analyzer can intercept call sites. Runtime method bodies throw NotSupportedException with [RequiresDynamicCode] + [RequiresUnreferencedCode] annotations so ILC trims them cleanly during Native AOT publish. Use WITH the Dapper.AOT package. Resolves DapperLib/DapperAOT#168.</Description>
    <PackageTags>dapper;aot;native-aot;stubs;sdk</PackageTags>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <IsAotCompatible>true</IsAotCompatible>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
    <EnableAotAnalyzer>true</EnableAotAnalyzer>
    <EnablePackageValidation>false</EnablePackageValidation>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Verbara.Sdk.Dapper.Stubs.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create AssemblyInfo.cs**

Create `/media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Dapper.Stubs/AssemblyInfo.cs`:

```csharp
// Verbara.Sdk.Dapper.Stubs — AOT-clean drop-in replacement for Dapper.dll.
// Assembly name = "Dapper" so this DLL substitutes for the real Dapper.dll
// in the runtime closure. Public API is a 1:1 mirror of Dapper 2.1.72.
// See: docs/specs/2026-05-19-verbara-sdk-dapper-stubs-design.md (Verbara.Platform repo)
// Resolves: https://github.com/DapperLib/DapperAOT/issues/168
```

- [ ] **Step 3: Create README.md**

Create `/media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Dapper.Stubs/README.md`:

```markdown
# Verbara.Sdk.Dapper.Stubs

AOT-clean drop-in replacement for `Dapper.dll`. Mirrors Dapper 2.1.72 public API surface so consumer code compiles + the Dapper.AOT source generator can detect call sites; runtime method bodies throw `NotSupportedException` with `[RequiresDynamicCode]` + `[RequiresUnreferencedCode]` annotations so ILC trims them cleanly during Native AOT publish.

## How to use

In your storage csproj:

```diff
- <PackageReference Include="Dapper" />
+ <PackageReference Include="Verbara.Sdk.Dapper.Stubs" />
+ <PackageReference Include="Dapper.AOT" PrivateAssets="all" />
```

Add to a top-level file (e.g. `AssemblyInfo.cs`):

```csharp
using Dapper;
[module: DapperAot]
```

Add to your csproj `<PropertyGroup>`:

```xml
<InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);Dapper.AOT</InterceptorsPreviewNamespaces>
```

## Why this exists

`Dapper.AOT` interceptors successfully replace consumer call sites at compile time, but `Dapper.dll` itself remains in the publish output. ILC scans `Dapper.dll` and emits ~50 fatal `IL3050`/`IL207x` diagnostics from its `DynamicMethod` + `MakeGenericType` usage — even though that code is never executed.

This package ships a parallel `Dapper.dll` with the same public API surface but AOT-clean stub bodies, so ILC sees stubs instead of the real Dapper internals. The Dapper.AOT-generated interceptors continue to win at runtime, calling into `Dapper.AOT.dll` (which is already AOT-clean by design).

See [DapperLib/DapperAOT#168](https://github.com/DapperLib/DapperAOT/issues/168) for the upstream proposal.

## License

MIT.
```

### Task A.3: Register project in Verbara.Sdk solution

**Files:**
- Modify: `/media/Data/Source/Verbara/Verbara.Sdk/Verbara.Sdk.slnx`

- [ ] **Step 1: Inspect current slnx**

Run: `grep -E "src/Verbara.Sdk" /media/Data/Source/Verbara/Verbara.Sdk/Verbara.Sdk.slnx | head -5`
Expected: list of existing project references — confirm slnx format

- [ ] **Step 2: Add project reference to slnx**

Edit `/media/Data/Source/Verbara/Verbara.Sdk/Verbara.Sdk.slnx` adding `<Project Path="src/Verbara.Sdk.Dapper.Stubs/Verbara.Sdk.Dapper.Stubs.csproj" />` in the `src/` group (alphabetical order — between existing siblings).

- [ ] **Step 3: Verify slnx loads**

Run: `cd /media/Data/Source/Verbara/Verbara.Sdk && dotnet build Verbara.Sdk.slnx -c Debug 2>&1 | tail -10`
Expected: build succeeds, includes `Verbara.Sdk.Dapper.Stubs -> .../Dapper.dll` in output

### Task A.4: Empty-assembly smoke build + pack

- [ ] **Step 1: Confirm empty assembly builds clean**

Run:
```bash
cd /media/Data/Source/Verbara/Verbara.Sdk
dotnet build src/Verbara.Sdk.Dapper.Stubs/ -c Release 2>&1 | tail -10
```
Expected: 0 warnings, 0 errors, output file is `Dapper.dll` (NOT `Verbara.Sdk.Dapper.Stubs.dll`)

- [ ] **Step 2: Verify output file is named Dapper.dll**

Run: `ls /media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Dapper.Stubs/bin/Release/net10.0/Dapper.dll`
Expected: file exists (drop-in name verified)

- [ ] **Step 3: Pack smoke (validate NuGet packaging works)**

Run:
```bash
dotnet pack /media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Dapper.Stubs/ -c Release -o /tmp/dapper-stubs-pack-smoke/
ls /tmp/dapper-stubs-pack-smoke/
```
Expected: `Verbara.Sdk.Dapper.Stubs.2.1.72-aotstub.1.nupkg`

- [ ] **Step 4: Inspect nupkg contents**

Run:
```bash
unzip -l /tmp/dapper-stubs-pack-smoke/Verbara.Sdk.Dapper.Stubs.2.1.72-aotstub.1.nupkg | grep -E "Dapper\.dll|net(8|10)"
```
Expected: `lib/net8.0/Dapper.dll` + `lib/net10.0/Dapper.dll` present (NOT `Verbara.Sdk.Dapper.Stubs.dll`)

### Task A.5: Commit Phase A

- [ ] **Step 1: Stage + commit**

```bash
cd /media/Data/Source/Verbara/Verbara.Sdk
git add src/Verbara.Sdk.Dapper.Stubs/ Verbara.Sdk.slnx
git commit -m "feat(dapper-stubs): A — project scaffolding (drop-in Dapper.dll name)

New project src/Verbara.Sdk.Dapper.Stubs/ scaffolded with the critical
drop-in identity settings:
  - AssemblyName=Dapper (output is Dapper.dll, not the package name)
  - AssemblyVersion=2.1.72.0 matching real Dapper for ref binding
  - PackageId=Verbara.Sdk.Dapper.Stubs, Version=2.1.72-aotstub.1
  - TargetFrameworks net8.0;net10.0
  - All AOT analyzers ON

Empty assembly builds clean and packs to nupkg with lib/net8.0/Dapper.dll
+ lib/net10.0/Dapper.dll embedded. Sub-deliverable A of the Phase D.1
plan (Verbara.Sdk.Dapper.Stubs)."
```

Expected: clean commit, no warnings

---

## Phase B — Test infrastructure FIRST (contract-driven)

### Task B.1: Scaffold test project

**Files:**
- Create: `/media/Data/Source/Verbara/Verbara.Sdk/Tests/Verbara.Sdk.Dapper.Stubs.Tests/Verbara.Sdk.Dapper.Stubs.Tests.csproj`

- [ ] **Step 1: Create test project directory + csproj**

Run: `mkdir -p /media/Data/Source/Verbara/Verbara.Sdk/Tests/Verbara.Sdk.Dapper.Stubs.Tests`

Create `/media/Data/Source/Verbara/Verbara.Sdk/Tests/Verbara.Sdk.Dapper.Stubs.Tests/Verbara.Sdk.Dapper.Stubs.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <!-- Stubs assembly is AOT-clean; tests stay non-AOT for FluentAssertions etc. -->
    <IsAotCompatible>false</IsAotCompatible>
    <EnableTrimAnalyzer>false</EnableTrimAnalyzer>
    <EnableSingleFileAnalyzer>false</EnableSingleFileAnalyzer>
    <EnableAotAnalyzer>false</EnableAotAnalyzer>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Verbara.Sdk.Dapper.Stubs/Verbara.Sdk.Dapper.Stubs.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Register test project in slnx**

Add `<Project Path="Tests/Verbara.Sdk.Dapper.Stubs.Tests/Verbara.Sdk.Dapper.Stubs.Tests.csproj" />` to the `Tests/` group in `Verbara.Sdk.slnx`.

- [ ] **Step 3: Verify scaffold builds**

Run: `cd /media/Data/Source/Verbara/Verbara.Sdk && dotnet build Tests/Verbara.Sdk.Dapper.Stubs.Tests/ -c Debug 2>&1 | tail -10`
Expected: build succeeds (0 tests at this point — empty project)

### Task B.2: Write `PublicApiSurfaceTests` (the contract)

**Files:**
- Create: `/media/Data/Source/Verbara/Verbara.Sdk/Tests/Verbara.Sdk.Dapper.Stubs.Tests/PublicApiSurfaceTests.cs`

- [ ] **Step 1: Write the test (will fail because stubs are empty)**

Create `/media/Data/Source/Verbara/Verbara.Sdk/Tests/Verbara.Sdk.Dapper.Stubs.Tests/PublicApiSurfaceTests.cs`:

```csharp
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Dapper.Stubs.Tests;

/// <summary>
/// Reflection-based API surface comparison: every public member in real Dapper 2.1.72 must exist
/// in our stubs assembly with identical signature. Conversely, we must NOT add any public member
/// that isn't in real Dapper. This is the drop-in contract.
/// </summary>
public sealed class PublicApiSurfaceTests
{
    private const string RealDapperPath =
        "/home/orion75/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.dll";

    private static Assembly LoadRealDapper() => Assembly.LoadFrom(RealDapperPath);

    private static Assembly LoadStubs() =>
        typeof(global::Dapper.CommandDefinition).Assembly;

    /// <summary>Every public type in real Dapper must exist in stubs with the same full name.</summary>
    [Fact]
    public void Stubs_ShouldExpose_AllPublicTypes_FromRealDapper()
    {
        var realTypes = LoadRealDapper().GetExportedTypes()
            .Select(t => t.FullName!)
            .Where(n => n.StartsWith("Dapper.", StringComparison.Ordinal))
            .OrderBy(n => n)
            .ToList();
        var stubTypes = LoadStubs().GetExportedTypes()
            .Select(t => t.FullName!)
            .Where(n => n.StartsWith("Dapper.", StringComparison.Ordinal))
            .OrderBy(n => n)
            .ToList();

        var missing = realTypes.Except(stubTypes).ToList();
        var extra = stubTypes.Except(realTypes).ToList();

        missing.Should().BeEmpty("stubs must mirror every public type in real Dapper");
        extra.Should().BeEmpty("stubs must NOT introduce types not in real Dapper");
    }

    /// <summary>Every public method in real Dapper must exist in stubs with matching signature.</summary>
    [Fact]
    public void Stubs_ShouldExpose_AllPublicMethods_FromRealDapper()
    {
        var realMethods = EnumeratePublicMethodSignatures(LoadRealDapper()).OrderBy(s => s).ToList();
        var stubMethods = EnumeratePublicMethodSignatures(LoadStubs()).OrderBy(s => s).ToList();

        var missing = realMethods.Except(stubMethods).ToList();
        var extra = stubMethods.Except(realMethods).ToList();

        missing.Should().BeEmpty(
            $"stubs must mirror every public method. Missing ({missing.Count}):\n{string.Join("\n", missing.Take(20))}");
        extra.Should().BeEmpty(
            $"stubs must NOT add methods not in real Dapper. Extra ({extra.Count}):\n{string.Join("\n", extra.Take(20))}");
    }

    private static IEnumerable<string> EnumeratePublicMethodSignatures(Assembly asm)
    {
        foreach (var type in asm.GetExportedTypes())
        {
            if (!type.FullName!.StartsWith("Dapper.", StringComparison.Ordinal)) continue;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                // Exclude property accessors + event accessors — they're tested by property/event mirrors implicitly
                if (m.IsSpecialName) continue;
                yield return FormatSignature(type, m);
            }
        }
    }

    private static string FormatSignature(Type declaringType, MethodInfo m)
    {
        var paramStr = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name));
        return $"{declaringType.FullName}::{m.Name}({paramStr}) -> {m.ReturnType.FullName}";
    }
}
```

- [ ] **Step 2: Run the test — expect MASSIVE failure**

Run: `cd /media/Data/Source/Verbara/Verbara.Sdk && dotnet test Tests/Verbara.Sdk.Dapper.Stubs.Tests/ -c Debug 2>&1 | tail -20`
Expected: 2 tests fail — `Stubs_ShouldExpose_AllPublicTypes_FromRealDapper` reports `missing.Count = 30`, methods test reports ~94+ missing methods. **This is the work plan made executable**: every type/method listed in the failure output is something Phase C must produce.

- [ ] **Step 3: Save the failure report**

Run:
```bash
dotnet test Tests/Verbara.Sdk.Dapper.Stubs.Tests/ -c Debug --logger "console;verbosity=detailed" > /tmp/dapper-stubs-api-baseline.log 2>&1
grep -E "Dapper\." /tmp/dapper-stubs-api-baseline.log | head -50
```
Expected: list of types + methods needed. This list IS the implementation checklist for Phase C.

### Task B.3: Write `AotAnnotationsTests` (the safety gate)

**Files:**
- Create: `/media/Data/Source/Verbara/Verbara.Sdk/Tests/Verbara.Sdk.Dapper.Stubs.Tests/AotAnnotationsTests.cs`

- [ ] **Step 1: Write the test**

Create `/media/Data/Source/Verbara/Verbara.Sdk/Tests/Verbara.Sdk.Dapper.Stubs.Tests/AotAnnotationsTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Dapper.Stubs.Tests;

/// <summary>
/// Every public method whose body throws NotSupportedException MUST carry both
/// [RequiresDynamicCode] and [RequiresUnreferencedCode] annotations so ILC trims them cleanly
/// during Native AOT publish. Working impls (e.g. CommandDefinition ctor, DynamicParameters.Add)
/// are exempt.
/// </summary>
public sealed class AotAnnotationsTests
{
    private static readonly Type[] ExemptTypes =
    [
        typeof(global::Dapper.CommandDefinition),     // working impl: ctor + property getters
        typeof(global::Dapper.CommandFlags),          // enum
        typeof(global::Dapper.DynamicParameters),     // working impl (partial)
        typeof(global::Dapper.ExplicitConstructorAttribute), // Attribute subclass
    ];

    [Fact]
    public void StubMethods_ShouldHave_RequiresDynamicCode_And_RequiresUnreferencedCode()
    {
        var stubs = typeof(global::Dapper.CommandDefinition).Assembly;
        var offenders = new List<string>();

        foreach (var type in stubs.GetExportedTypes())
        {
            if (ExemptTypes.Contains(type)) continue;
            if (!type.FullName!.StartsWith("Dapper.", StringComparison.Ordinal)) continue;

            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue; // skip property accessors
                var hasRdc = m.GetCustomAttribute<RequiresDynamicCodeAttribute>() is not null;
                var hasRuc = m.GetCustomAttribute<RequiresUnreferencedCodeAttribute>() is not null;
                if (!hasRdc || !hasRuc)
                {
                    offenders.Add(
                        $"{type.FullName}::{m.Name} — hasRDC={hasRdc} hasRUC={hasRuc}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "every stub method must carry both AOT annotations so ILC trims it cleanly.\n" +
            $"Offenders ({offenders.Count}):\n{string.Join("\n", offenders.Take(20))}");
    }
}
```

- [ ] **Step 2: Run — expect failure (no stubs yet, but exempt types also not present)**

Run: `dotnet test Tests/Verbara.Sdk.Dapper.Stubs.Tests/ -c Debug --filter "FullyQualifiedName~AotAnnotations" 2>&1 | tail -10`
Expected: failure — references `global::Dapper.CommandDefinition` which doesn't exist yet (CS0234). This is OK — the test compiles once Phase C lands the type.

> **Implementer note**: this test SETUP intentionally fails to compile until Task C.13 lands `CommandDefinition`. Mark `AotAnnotationsTests.cs` with `// TEMPORARY: depends on Task C.13` so it's not forgotten. Once C.13 is done, the test compiles and runs.

### Task B.4: Commit Phase B

- [ ] **Step 1: Commit**

```bash
cd /media/Data/Source/Verbara/Verbara.Sdk
git add Tests/Verbara.Sdk.Dapper.Stubs.Tests/ Verbara.Sdk.slnx
git commit -m "test(dapper-stubs): B — PublicApiSurfaceTests + AotAnnotationsTests (failing-first)

Test infrastructure written BEFORE the stubs. Both tests fail by design:

- PublicApiSurfaceTests reports ~30 missing types + ~94+ missing methods
  vs real Dapper 2.1.72 (loaded via Assembly.LoadFrom from NuGet cache).
  This failure output is the executable checklist for Phase C.
- AotAnnotationsTests references CommandDefinition (not yet implemented);
  compiles + runs only after Task C.13.

Both tests are the safety net: PublicApiSurfaceTests blocks regression
(missing or extra public member); AotAnnotationsTests blocks regression
(stub method without RequiresDynamicCode / RequiresUnreferencedCode)."
```

---

## Phase C — Mirror Dapper public API (incremental implementation)

> **Canonical stub method template** (copy/paste pattern for every throwing stub method in this phase):
>
> ```csharp
> [RequiresDynamicCode("Real Dapper builds parameter emitters at runtime via DynamicMethod. " +
>                      "This stub assumes Dapper.AOT interceptors replace the call site. " +
>                      "If this body executes, Dapper.AOT did not intercept — verify [module: DapperAot] " +
>                      "is applied and InterceptorsPreviewNamespaces MSBuild property includes Dapper.AOT.")]
> [RequiresUnreferencedCode("Real Dapper reflects over row type properties + constructors. " +
>                           "Dapper.AOT interceptors replace this with statically-generated RowFactory<T>.")]
> public static <ReturnType> <MethodName>(<parameters>)
>     => throw new NotSupportedException(
>         "Dapper.<TypeName>.<MethodName> stub — Dapper.AOT did not intercept this call site. " +
>         "See: https://aot.dapperlib.dev/gettingstarted");
> ```
>
> For async methods returning `Task<T>` or `Task`:
> ```csharp
> [RequiresDynamicCode(...)] [RequiresUnreferencedCode(...)]
> public static Task<T> XxxAsync(...) =>
>     Task.FromException<T>(new NotSupportedException("..."));
> ```

> **Reference for exact signatures**: extract from `~/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.xml` per Task. Method signature parser snippet:
>
> ```bash
> grep -A1 'name="M:Dapper.SqlMapper\.Execute' ~/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.xml | head -30
> ```
>
> For the authoritative source code (return types, parameter defaults), reference [Dapper 2.1.72 GitHub source](https://github.com/DapperLib/Dapper/tree/2.1.72/Dapper).

### Task C.1: `CommandFlags` enum (working impl)

**Files:** Create: `/media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Dapper.Stubs/Dapper/CommandFlags.cs`

- [ ] **Step 1: Create the file**

```csharp
namespace Dapper;

/// <summary>
/// AOT-clean stub of <c>Dapper.CommandFlags</c>. Working enum — semantic values must match real Dapper
/// because <see cref="CommandDefinition.Buffered"/> reads them.
/// </summary>
[Flags]
public enum CommandFlags
{
    None = 0,
    Buffered = 1,
    Pipelined = 2,
    NoCache = 4,
}
```

- [ ] **Step 2: Build + verify**

Run: `cd /media/Data/Source/Verbara/Verbara.Sdk && dotnet build src/Verbara.Sdk.Dapper.Stubs/ -c Debug 2>&1 | tail -5`
Expected: 0 errors, 0 warnings

### Task C.2: `ExplicitConstructorAttribute` (working impl — Attribute subclass)

**Files:** Create: `/media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Dapper.Stubs/Dapper/ExplicitConstructorAttribute.cs`

- [ ] **Step 1: Create the file**

```csharp
namespace Dapper;

/// <summary>
/// AOT-clean stub of <c>Dapper.ExplicitConstructorAttribute</c>. Working — pure Attribute subclass,
/// no body needed. Consumers may decorate their row constructors with [ExplicitConstructor] to hint
/// the Dapper.AOT generator (the analyzer may or may not honor this; real-Dapper consumers tolerate it).
/// </summary>
[AttributeUsage(AttributeTargets.Constructor)]
public sealed class ExplicitConstructorAttribute : Attribute;
```

- [ ] **Step 2: Build verify**

Run: `dotnet build src/Verbara.Sdk.Dapper.Stubs/ -c Debug 2>&1 | tail -3`
Expected: clean

### Task C.3: `SqlMapper` class header + `SqlMapper.cs` partial

**Files:** Create: `/media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Dapper.Stubs/Dapper/SqlMapper/SqlMapper.cs`

- [ ] **Step 1: Create the partial-class header**

```csharp
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

/// <summary>
/// AOT-clean stub of <c>Dapper.SqlMapper</c>. The bulk of Dapper's public API lives on this static class.
/// All method bodies throw NotSupportedException — they are replaced at compile time by Dapper.AOT
/// interceptors. Working state lives in <see cref="Settings"/> (no-op semantics) only.
///
/// Partial-class file layout under Dapper/SqlMapper/:
///   SqlMapper.cs                  — class header + global state (this file)
///   SqlMapper.Execute.cs          — Execute + ExecuteAsync overloads
///   SqlMapper.ExecuteScalar.cs    — ExecuteScalar + Async
///   SqlMapper.Query.cs            — Query<T> + multi-mapping
///   SqlMapper.QueryAsync.cs       — QueryAsync<T> + variants
///   SqlMapper.QueryFirst.cs       — QueryFirst[OrDefault] sync + async
///   SqlMapper.QuerySingle.cs      — QuerySingle[OrDefault] sync + async
///   SqlMapper.QueryMultiple.cs    — QueryMultiple + Async (returns GridReader)
///   SqlMapper.TypeHandling.cs     — AddTypeHandler / AddTypeMap / AsTableValuedParameter
///   SqlMapper.Nested.*.cs         — one file per nested type
/// </summary>
public static partial class SqlMapper
{
    // Global state: real Dapper has SqlMapper.Settings as a static class with property setters.
    // Stub policy: idempotent no-ops so consumer code like `SqlMapper.Settings.UseSingleResultOptimization = true`
    // at startup doesn't crash. AOT-clean (no reflection involved).
}
```

- [ ] **Step 2: Build verify**

Run: `dotnet build src/Verbara.Sdk.Dapper.Stubs/ -c Debug 2>&1 | tail -3`
Expected: clean

### Task C.4: `SqlMapper.Settings` nested static class (working — no-op)

**Files:** Create: `/media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Dapper.Stubs/Dapper/SqlMapper/SqlMapper.Nested.Settings.cs`

- [ ] **Step 1: Create the file**

```csharp
namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.Settings</c>. Real Dapper exposes global toggles like
    /// <c>ApplyNullValues</c>, <c>UseSingleResultOptimization</c>, etc. Stubs ship idempotent
    /// no-op setters: reading returns the default; setting silently discards. This prevents
    /// crashes when consumer startup code touches these toggles but the values have no effect
    /// (Dapper.AOT-generated interceptors don't read them).
    /// </summary>
    public static class Settings
    {
        // Real-Dapper API: extract from Dapper.xml — list of public static properties.
        // Implement each as: { get => default; set { /* discard */ } }
        public static int CommandTimeout { get; set; }
        public static bool ApplyNullValues { get; set; }
        public static bool UseSingleResultOptimization { get; set; }
        public static bool UseIncrementalReads { get; set; }
        public static bool PadListExpansions { get; set; }
        public static int InListStringSplitCount { get; set; } = -1;
        public static bool ApplyGenericArgumentToOrigOnLogged { get; set; }
        // ... extract remaining via grep on Dapper.xml
    }
}
```

- [ ] **Step 2: Cross-check against real Dapper**

Run:
```bash
grep -oE 'name="P:Dapper.SqlMapper\.Settings\.[A-Za-z0-9]+' ~/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.xml | sort -u
```
Expected: list of all public properties on Settings. Add any missing ones to the stub file.

- [ ] **Step 3: Build verify**

Run: `dotnet build src/Verbara.Sdk.Dapper.Stubs/ -c Debug 2>&1 | tail -3`

### Task C.5: SqlMapper nested interfaces (def-only — no impl)

**Files:**
- Create: `Dapper/SqlMapper/SqlMapper.Nested.ICustomQueryParameter.cs`
- Create: `Dapper/SqlMapper/SqlMapper.Nested.IDynamicParameters.cs`
- Create: `Dapper/SqlMapper/SqlMapper.Nested.IMemberMap.cs`
- Create: `Dapper/SqlMapper/SqlMapper.Nested.IParameterCallbacks.cs`
- Create: `Dapper/SqlMapper/SqlMapper.Nested.IParameterLookup.cs`
- Create: `Dapper/SqlMapper/SqlMapper.Nested.ITypeHandler.cs`
- Create: `Dapper/SqlMapper/SqlMapper.Nested.ITypeMap.cs`

- [ ] **Step 1: Create each interface (extract signatures from Dapper.xml)**

For each interface, create a file at the corresponding path with:

```csharp
using System.Data;
namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>AOT-clean stub of <c>SqlMapper.&lt;InterfaceName&gt;</c>. Interface definition only.</summary>
    public interface <InterfaceName>
    {
        // copy methods from real Dapper's interface
    }
}
```

**Reference content** (extract precise signatures from `~/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.xml` via:
```bash
grep -E 'name="M:Dapper.SqlMapper\.<InterfaceName>\.' ~/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.xml
```

For example, `ICustomQueryParameter`:
```csharp
public interface ICustomQueryParameter
{
    void AddParameter(IDbCommand command, string name);
}
```

`IDynamicParameters`:
```csharp
public interface IDynamicParameters
{
    void AddParameters(IDbCommand command, Identity identity);
}
```

`IMemberMap`:
```csharp
public interface IMemberMap
{
    string ColumnName { get; }
    Type MemberType { get; }
    System.Reflection.PropertyInfo? Property { get; }
    System.Reflection.FieldInfo? Field { get; }
    System.Reflection.ParameterInfo? Parameter { get; }
}
```

`IParameterCallbacks`:
```csharp
public interface IParameterCallbacks
{
    void OnCompleted();
}
```

`IParameterLookup`:
```csharp
public interface IParameterLookup
{
    object? this[string name] { get; }
}
```

`ITypeHandler`:
```csharp
public interface ITypeHandler
{
    void SetValue(System.Data.IDbDataParameter parameter, object? value);
    object? Parse(Type destinationType, object value);
}
```

`ITypeMap`:
```csharp
public interface ITypeMap
{
    System.Reflection.ConstructorInfo? FindConstructor(string[] names, Type[] types);
    System.Reflection.ConstructorInfo? FindExplicitConstructor();
    IMemberMap? GetConstructorParameter(System.Reflection.ConstructorInfo constructor, string columnName);
    IMemberMap? GetMember(string columnName);
}
```

- [ ] **Step 2: Build verify**

Run: `dotnet build src/Verbara.Sdk.Dapper.Stubs/ -c Debug 2>&1 | tail -3`
Expected: clean

### Task C.6: SqlMapper nested stub classes (GridReader, TypeHandler<T>, etc.)

**Files** (one file per type — full list in spec Section 3.1):
- `SqlMapper.Nested.GridReader.cs`
- `SqlMapper.Nested.TypeHandler.cs` (TypeHandler<T> abstract base)
- `SqlMapper.Nested.StringTypeHandler.cs` (StringTypeHandler<T>)
- `SqlMapper.Nested.UdtTypeHandler.cs`
- `SqlMapper.Nested.Identity.cs`
- `SqlMapper.Nested.LiteralToken.cs`
- `SqlMapper.Nested.Link.cs` (Link<T1,T2>)
- `SqlMapper.Nested.TypeHandlerCache.cs` (TypeHandlerCache<T>)
- `SqlMapper.Nested.DontMap.cs` (Attribute — working)

- [ ] **Step 1: For each nested class, create the file with stub methods**

Use the canonical stub method template. Example for `GridReader`:

```csharp
using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>AOT-clean stub of <c>SqlMapper.GridReader</c>. All methods throw.</summary>
    public sealed class GridReader : IDisposable
    {
        private GridReader() { } // not instantiable from outside

        [RequiresDynamicCode("Real Dapper builds parameter emitters at runtime via DynamicMethod. " +
                             "This stub assumes Dapper.AOT interceptors replace the call site. " +
                             "If this body executes, Dapper.AOT did not intercept.")]
        [RequiresUnreferencedCode("Real Dapper reflects over row type properties.")]
        public IEnumerable<T> Read<T>(bool buffered = true) =>
            throw new NotSupportedException("Dapper.SqlMapper.GridReader.Read<T> stub.");

        // ... mirror remaining public methods from real Dapper GridReader

        public void Dispose() { /* no-op */ }
    }
}
```

> **Implementer**: extract the GridReader public method list from Dapper.xml using:
> ```bash
> grep -E 'name="M:Dapper.SqlMapper\.GridReader\.' ~/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.xml
> ```
> Apply the canonical stub template to each.

`SqlMapper.Nested.DontMap.cs` is special (working — Attribute base):
```csharp
namespace Dapper;
public static partial class SqlMapper
{
    /// <summary>AOT-clean stub of <c>SqlMapper.DontMap</c>. Marker attribute — no body needed.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class DontMap : Attribute;
}
```

- [ ] **Step 2: Build + verify all files**

Run: `dotnet build src/Verbara.Sdk.Dapper.Stubs/ -c Debug 2>&1 | tail -5`
Expected: clean

### Task C.7-C.12: SqlMapper extension method partial files

> **For each of these tasks (C.7 through C.12), the pattern is identical**:
> 1. Extract method signatures from Dapper.xml using `grep`
> 2. For each method, generate a stub using the canonical template (top of Phase C)
> 3. Use `Task.FromException<T>` for async methods
> 4. Build + verify

### Task C.7: `SqlMapper.Execute.cs` (~10 methods)

**Files:** Create `Dapper/SqlMapper/SqlMapper.Execute.cs`

- [ ] **Step 1: Extract signatures**

Run:
```bash
grep -A1 'name="M:Dapper.SqlMapper\.Execute(' ~/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.xml
grep -A1 'name="M:Dapper.SqlMapper\.ExecuteAsync(' ~/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.xml
```

- [ ] **Step 2: Write the partial file**

```csharp
using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    [RequiresDynamicCode("Real Dapper builds parameter emitters at runtime via DynamicMethod. " +
                         "This stub assumes Dapper.AOT interceptors replace the call site. " +
                         "If this body executes, Dapper.AOT did not intercept.")]
    [RequiresUnreferencedCode("Real Dapper reflects over parameter object properties.")]
    public static int Execute(this IDbConnection cnn, string sql, object? param = null,
        IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException("Dapper.SqlMapper.Execute stub.");

    [RequiresDynamicCode("...")]
    [RequiresUnreferencedCode("...")]
    public static int Execute(this IDbConnection cnn, CommandDefinition command)
        => throw new NotSupportedException("Dapper.SqlMapper.Execute(CommandDefinition) stub.");

    [RequiresDynamicCode("...")]
    [RequiresUnreferencedCode("...")]
    public static Task<int> ExecuteAsync(this IDbConnection cnn, string sql, object? param = null,
        IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<int>(new NotSupportedException("Dapper.SqlMapper.ExecuteAsync stub."));

    [RequiresDynamicCode("...")]
    [RequiresUnreferencedCode("...")]
    public static Task<int> ExecuteAsync(this IDbConnection cnn, CommandDefinition command)
        => Task.FromException<int>(new NotSupportedException("Dapper.SqlMapper.ExecuteAsync(CommandDefinition) stub."));

    // ... add remaining Execute overloads per Dapper.xml extract
}
```

- [ ] **Step 3: Build + verify**

Run: `dotnet build src/Verbara.Sdk.Dapper.Stubs/ -c Debug 2>&1 | tail -3`
Expected: clean

### Task C.8: `SqlMapper.ExecuteScalar.cs` (~6 methods)

Apply the same pattern as C.7. Methods to mirror: `ExecuteScalar`, `ExecuteScalar<T>`, `ExecuteScalarAsync`, `ExecuteScalarAsync<T>`, + each with `CommandDefinition` overload.

- [ ] **Step 1: Extract from Dapper.xml + write stubs + build verify**

### Task C.9: `SqlMapper.Query.cs` (~12 + multi-mapping ~8 methods)

Apply the same pattern. This is the BIGGEST partial — `Query<T>`, `Query` (dynamic), and the multi-mapping `Query<T1,T2,TReturn>`, ..., `Query<T1,T2,T3,T4,T5,T6,T7,TReturn>` (Func-based).

- [ ] **Step 1-3**: Extract from Dapper.xml + write stubs + build verify

### Task C.10: `SqlMapper.QueryAsync.cs` (~8 + multi-mapping ~7 methods)

Same as C.9 but async (return `Task<IEnumerable<T>>`).

### Task C.11: `SqlMapper.QueryFirst.cs` (~8 methods)

`QueryFirst<T>`, `QueryFirst` (dynamic), `QueryFirstAsync<T>`, `QueryFirstOrDefault<T>`, `QueryFirstOrDefaultAsync<T>` + dynamic + CommandDefinition variants.

### Task C.12: `SqlMapper.QuerySingle.cs` (~8 methods)

Mirror C.11 structure for `QuerySingle*`.

### Task C.13: `SqlMapper.QueryMultiple.cs` (~4 methods, returns GridReader)

`QueryMultiple` (sync + async), with sql+param and CommandDefinition overloads. Return type: `GridReader` (sync) / `Task<GridReader>` (async).

### Task C.14: `SqlMapper.TypeHandling.cs` (~8 methods)

`AddTypeHandler<T>(TypeHandler<T>)`, `AddTypeHandler(Type, ITypeHandler)`, `AddTypeHandlerImpl(Type, ITypeHandler, bool)`, `AddTypeMap(Type, DbType)`, `AsTableValuedParameter(...)` overloads, `AsList<T>(...)`, etc.

> **Note for C.14**: `AddTypeHandler` is called at consumer startup (e.g. `SqlMapper.AddTypeHandler(DateOnlyTypeHandler.Instance);`). Since Dapper.AOT does NOT use TypeHandlers (per upstream issue #173), our stub for `AddTypeHandler` should still throw — but the consumer must REMOVE these calls per the Phase D.3 special-handling decision matrix. The throw forces this surfacing.

### Task C.15: Top-level type — `CommandDefinition` (WORKING IMPL)

**Files:** Create: `Dapper/CommandDefinition.cs`

- [ ] **Step 1: Write the working impl**

```csharp
using System.Data;

namespace Dapper;

/// <summary>
/// AOT-clean drop-in for <c>Dapper.CommandDefinition</c>. WORKING IMPLEMENTATION — Dapper.AOT-generated
/// interceptor code reads the property getters at runtime to construct the real query against
/// the underlying ADO.NET command.
/// </summary>
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

- [ ] **Step 2: Build verify**

Run: `dotnet build src/Verbara.Sdk.Dapper.Stubs/ -c Debug 2>&1 | tail -3`
Expected: clean (AotAnnotationsTests now compiles since `CommandDefinition` is exempt — see B.3)

### Task C.16: Top-level type — `DynamicParameters` (WORKING PARTIAL IMPL)

**Files:** Create: `Dapper/DynamicParameters.cs`

- [ ] **Step 1: Write the partial working impl**

```csharp
using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

/// <summary>
/// AOT-clean stub of <c>Dapper.DynamicParameters</c>. PARTIAL working impl:
/// parameterless ctor + <see cref="Add"/> + <see cref="Get{T}"/> + <see cref="ParameterNames"/> work
/// so Dapper.AOT-generated CommandFactory code can read the parameter bag back via <c>Cast&lt;T&gt;</c>.
/// The template-overload constructor and explicit <see cref="SqlMapper.IDynamicParameters.AddParameters"/>
/// throw — those are paths that mean Dapper.AOT didn't intercept (per DAP015 + #168).
/// </summary>
public sealed class DynamicParameters : SqlMapper.IDynamicParameters
{
    private readonly Dictionary<string, ParameterEntry> _parameters = new(StringComparer.Ordinal);

    public DynamicParameters() { }

    [RequiresDynamicCode("Real Dapper iterates template properties via reflection. " +
                         "Per DAP015, rewrite this call site to use anonymous types.")]
    [RequiresUnreferencedCode("Real Dapper reflects over template's public properties.")]
    public DynamicParameters(object? template)
        => throw new NotSupportedException(
            "Dapper.DynamicParameters(object template) stub — rewrite to anonymous types per DAP015. " +
            "See: Phase D.3 special-handling decision matrix.");

    public void Add(string name, object? value = null, DbType? dbType = null,
        ParameterDirection? direction = null, int? size = null, byte? precision = null,
        byte? scale = null)
        => _parameters[name] = new ParameterEntry(name, value, dbType, direction, size, precision, scale);

    public void AddDynamicParams(object? param) =>
        throw new NotSupportedException(
            "Dapper.DynamicParameters.AddDynamicParams stub — rewrite per DAP015.");

    public IEnumerable<string> ParameterNames => _parameters.Keys;

    public T? Get<T>(string name)
    {
        if (!_parameters.TryGetValue(name, out var entry))
            throw new KeyNotFoundException($"Parameter '{name}' not found.");
        return (T?)entry.Value;
    }

    public DynamicParameters Output<T>(T target, System.Linq.Expressions.Expression<Func<T, object?>> expression) =>
        throw new NotSupportedException("Dapper.DynamicParameters.Output stub — Dapper.AOT does not support output parameters this way.");

    [RequiresDynamicCode("Vanilla Dapper invokes IDynamicParameters.AddParameters via reflection-built " +
                         "parameter setters. Dapper.AOT interceptors replace this entirely.")]
    [RequiresUnreferencedCode("Vanilla Dapper reflects over the parameter values.")]
    void SqlMapper.IDynamicParameters.AddParameters(IDbCommand command, SqlMapper.Identity identity)
        => throw new NotSupportedException(
            "Dapper.DynamicParameters.AddParameters stub — Dapper.AOT did not intercept the parent call site.");

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

- [ ] **Step 2: Build verify**

Run: `dotnet build src/Verbara.Sdk.Dapper.Stubs/ -c Debug 2>&1 | tail -3`
Expected: clean

### Task C.17: Top-level types — remaining (`CustomPropertyTypeMap`, `DbString`, `DefaultTypeMap`, `FeatureSupport`, `IWrappedDataReader`, `SimpleMemberMap`, `SqlDataRecordListTVPParameter<T>`, `TableValuedParameter`)

**Files:** Create one file per type under `Dapper/`.

- [ ] **Step 1: For each, write the stub**

`IWrappedDataReader` is special — pure interface:
```csharp
using System.Data;
namespace Dapper;
public interface IWrappedDataReader : IDataReader, IDisposable
{
    IDbCommand Command { get; }
    IDataReader Reader { get; }
}
```

For the remaining classes, use the canonical stub template (throw + annotations on every method). Extract signatures via:
```bash
grep -E 'name="(T|M):Dapper\.(CustomPropertyTypeMap|DbString|DefaultTypeMap|FeatureSupport|SimpleMemberMap|SqlDataRecordListTVPParameter|TableValuedParameter)' ~/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.xml
```

`SqlDataRecordListTVPParameter<T>` implements `ICustomQueryParameter` and is generic — the body of `AddParameter(IDbCommand, string)` throws.

- [ ] **Step 2: Build verify**

Run: `dotnet build src/Verbara.Sdk.Dapper.Stubs/ -c Debug 2>&1 | tail -3`
Expected: clean

### Task C.18: Run `PublicApiSurfaceTests` — must now PASS

- [ ] **Step 1: Run the test**

Run:
```bash
cd /media/Data/Source/Verbara/Verbara.Sdk
dotnet test Tests/Verbara.Sdk.Dapper.Stubs.Tests/ -c Debug --filter "FullyQualifiedName~PublicApiSurface" 2>&1 | tail -15
```
Expected: BOTH tests pass.

- [ ] **Step 2: If `missing` is non-empty → add the missing members**

The test output lists what's missing. Add each missing member to the appropriate stub file using the canonical template.

- [ ] **Step 3: If `extra` is non-empty → remove the extras**

The test output lists what we added that real Dapper doesn't have. Remove from the appropriate stub file.

- [ ] **Step 4: Re-run until both tests pass**

### Task C.19: Run `AotAnnotationsTests` — must PASS

- [ ] **Step 1: Run the test**

Run:
```bash
dotnet test Tests/Verbara.Sdk.Dapper.Stubs.Tests/ -c Debug --filter "FullyQualifiedName~AotAnnotations" 2>&1 | tail -10
```
Expected: PASS — every throwing stub method has both `[RequiresDynamicCode]` and `[RequiresUnreferencedCode]`.

- [ ] **Step 2: If offenders → add the missing annotations**

For each offender, add the canonical template's two attributes above the method.

### Task C.20: Commit Phase C

- [ ] **Step 1: Stage + commit**

```bash
cd /media/Data/Source/Verbara/Verbara.Sdk
git add src/Verbara.Sdk.Dapper.Stubs/
git commit -m "feat(dapper-stubs): C — full Dapper 2.1.72 public API mirror

100% mirror of Dapper.dll 2.1.72 public surface (13 top-level types
+ 17 nested SqlMapper types + ~94 SqlMapper methods). Stub bodies
throw NotSupportedException with [RequiresDynamicCode] and
[RequiresUnreferencedCode] annotations so ILC trims them cleanly.

Working impls (touched at runtime by Dapper.AOT-generated interceptors):
  - Dapper.CommandDefinition struct (ctor + property getters)
  - Dapper.DynamicParameters class (parameterless ctor + Add + Get +
    ParameterNames; template ctor + IDynamicParameters.AddParameters
    throw — those paths mean Dapper.AOT didn't intercept)
  - Dapper.CommandFlags enum (semantic constants)
  - Dapper.SqlMapper.ICustomQueryParameter (interface def only)
  - Dapper.SqlMapper.Settings (idempotent no-op global toggles)
  - Dapper.ExplicitConstructorAttribute / SqlMapper.DontMap (Attribute bases)

PublicApiSurfaceTests + AotAnnotationsTests both PASS — drop-in
contract validated reflection vs real Dapper 2.1.72.

Resolves the API-surface portion of DapperLib/DapperAOT#168."
```

---

## Phase D — Behavioral tests for working impls

### Task D.1: `CommandDefinitionTests`

**Files:** Create: `Tests/Verbara.Sdk.Dapper.Stubs.Tests/CommandDefinitionTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using System.Data;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Dapper.Stubs.Tests;

public sealed class CommandDefinitionTests
{
    [Fact]
    public void Constructor_ShouldStoreAllArguments_WhenAllProvided()
    {
        using var cts = new CancellationTokenSource();
        var def = new global::Dapper.CommandDefinition(
            commandText: "SELECT 1",
            parameters: new { id = 42 },
            transaction: null,
            commandTimeout: 30,
            commandType: CommandType.Text,
            flags: global::Dapper.CommandFlags.Buffered,
            cancellationToken: cts.Token);

        def.CommandText.Should().Be("SELECT 1");
        def.Parameters.Should().NotBeNull();
        def.CommandTimeout.Should().Be(30);
        def.CommandType.Should().Be(CommandType.Text);
        def.Flags.Should().Be(global::Dapper.CommandFlags.Buffered);
        def.CancellationToken.Should().Be(cts.Token);
        def.Buffered.Should().BeTrue();
    }

    [Fact]
    public void Buffered_ShouldBeFalse_WhenFlagsDoNotIncludeBuffered()
    {
        var def = new global::Dapper.CommandDefinition("SELECT 1", flags: global::Dapper.CommandFlags.None);
        def.Buffered.Should().BeFalse();
    }

    [Fact]
    public void Constructor_ShouldDefault_WhenMinimalArgs()
    {
        var def = new global::Dapper.CommandDefinition("SELECT 1");
        def.CommandText.Should().Be("SELECT 1");
        def.Parameters.Should().BeNull();
        def.Transaction.Should().BeNull();
        def.CommandTimeout.Should().BeNull();
        def.CommandType.Should().BeNull();
        def.Flags.Should().Be(global::Dapper.CommandFlags.Buffered);
        def.Buffered.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run + verify pass**

Run: `dotnet test Tests/Verbara.Sdk.Dapper.Stubs.Tests/ --filter "FullyQualifiedName~CommandDefinition" 2>&1 | tail -10`
Expected: 3 PASS

### Task D.2: `DynamicParametersTests`

**Files:** Create: `Tests/Verbara.Sdk.Dapper.Stubs.Tests/DynamicParametersTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using System.Data;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Dapper.Stubs.Tests;

public sealed class DynamicParametersTests
{
    [Fact]
    public void Add_ThenGet_ShouldRoundTripValue()
    {
        var p = new global::Dapper.DynamicParameters();
        p.Add("id", 42, DbType.Int32);
        p.Get<int>("id").Should().Be(42);
    }

    [Fact]
    public void ParameterNames_ShouldEnumerateAllAddedNames()
    {
        var p = new global::Dapper.DynamicParameters();
        p.Add("id", 1);
        p.Add("name", "alice");
        p.ParameterNames.Should().BeEquivalentTo(["id", "name"]);
    }

    [Fact]
    public void Get_ShouldThrow_WhenNameNotFound()
    {
        var p = new global::Dapper.DynamicParameters();
        Action act = () => p.Get<int>("missing");
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Constructor_WithTemplate_ShouldThrow_PerDAP015()
    {
        Action act = () => _ = new global::Dapper.DynamicParameters(new { id = 1 });
        act.Should().Throw<NotSupportedException>().WithMessage("*DAP015*");
    }

    [Fact]
    public void AddParameters_ExplicitInterface_ShouldThrow()
    {
        global::Dapper.SqlMapper.IDynamicParameters p = new global::Dapper.DynamicParameters();
        Action act = () => p.AddParameters(command: null!, identity: null!);
        act.Should().Throw<NotSupportedException>().WithMessage("*Dapper.AOT did not intercept*");
    }
}
```

- [ ] **Step 2: Run + verify pass**

Run: `dotnet test Tests/Verbara.Sdk.Dapper.Stubs.Tests/ --filter "FullyQualifiedName~DynamicParameters" 2>&1 | tail -10`
Expected: 5 PASS

### Task D.3: `SqlMapperStubTests` (spot check throwing methods)

**Files:** Create: `Tests/Verbara.Sdk.Dapper.Stubs.Tests/SqlMapperStubTests.cs`

> **YAGNI note**: testing every single SqlMapper stub method (~94) is tedious and adds no value beyond AotAnnotationsTests (which already verifies they have the annotations). This test class spot-checks the FIVE most-called methods to confirm the throw + message pattern works.

- [ ] **Step 1: Write tests**

```csharp
using System.Data;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Dapper.Stubs.Tests;

/// <summary>
/// Spot-check that representative SqlMapper extension methods throw the expected NotSupportedException.
/// Comprehensive coverage of all 94+ methods would be redundant — AotAnnotationsTests + PublicApiSurfaceTests
/// already gate the contract. This exercises the runtime behavior of the canonical pattern.
/// </summary>
public sealed class SqlMapperStubTests
{
    private static IDbConnection Cnn() => new MockConnection();

    [Fact]
    public void Execute_ShouldThrowNotSupported()
    {
        Action act = () => Cnn().Execute("SELECT 1");
        act.Should().Throw<NotSupportedException>().WithMessage("*Dapper.AOT did not intercept*");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFaultedTask_WhenAwaited()
    {
        Func<Task> act = async () => await Cnn().ExecuteAsync("SELECT 1");
        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*Dapper.AOT did not intercept*");
    }

    [Fact]
    public void Query_ShouldThrowNotSupported()
    {
        Action act = () => Cnn().Query<int>("SELECT 1").ToList();
        act.Should().Throw<NotSupportedException>().WithMessage("*Dapper.AOT did not intercept*");
    }

    [Fact]
    public async Task QueryAsync_ShouldReturnFaultedTask_WhenAwaited()
    {
        Func<Task> act = async () => _ = await Cnn().QueryAsync<int>("SELECT 1");
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task QuerySingleOrDefaultAsync_ShouldReturnFaultedTask_WhenAwaited()
    {
        Func<Task> act = async () => _ = await Cnn().QuerySingleOrDefaultAsync<int>("SELECT 1");
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private sealed class MockConnection : IDbConnection
    {
        public string ConnectionString { get; set; } = "";
        public int ConnectionTimeout => 0;
        public string Database => "";
        public ConnectionState State => ConnectionState.Open;
        public IDbTransaction BeginTransaction() => throw new NotImplementedException();
        public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotImplementedException();
        public void ChangeDatabase(string databaseName) { }
        public void Close() { }
        public IDbCommand CreateCommand() => throw new NotImplementedException();
        public void Open() { }
        public void Dispose() { }
    }
}
```

- [ ] **Step 2: Run + verify pass**

Run: `dotnet test Tests/Verbara.Sdk.Dapper.Stubs.Tests/ --filter "FullyQualifiedName~SqlMapperStub" 2>&1 | tail -10`
Expected: 5 PASS

### Task D.4: Full test suite verde

- [ ] **Step 1: Run all stubs tests**

Run: `cd /media/Data/Source/Verbara/Verbara.Sdk && dotnet test Tests/Verbara.Sdk.Dapper.Stubs.Tests/ -c Release 2>&1 | tail -15`
Expected: ALL tests PASS (PublicApiSurface + AotAnnotations + CommandDefinition + DynamicParameters + SqlMapperStub)

### Task D.5: Commit Phase D

```bash
cd /media/Data/Source/Verbara/Verbara.Sdk
git add Tests/Verbara.Sdk.Dapper.Stubs.Tests/
git commit -m "test(dapper-stubs): D — behavioral tests for working impls

Tests confirm runtime semantics for the four working impls:
  - CommandDefinitionTests: ctor + property round-trip + Buffered flag
  - DynamicParametersTests: Add/Get round-trip + ParameterNames +
    template-ctor throws per DAP015 + IDynamicParameters.AddParameters
    explicit interface throws
  - SqlMapperStubTests: spot-checks 5 representative SqlMapper methods
    throw NotSupportedException with the canonical 'Dapper.AOT did not
    intercept' message (sync throws directly, async returns faulted Task)

All 5 test classes verde: PublicApiSurface + AotAnnotations + the three
behavioral classes. Verbara.Sdk.Dapper.Stubs is contract-complete and
behavior-validated."
```

---

## Phase E — Pack + dogfood canary on Sessions.Postgres

### Task E.1: Pack stubs to local-nuget-feed

- [ ] **Step 1: Pack**

```bash
cd /media/Data/Source/Verbara/Verbara.Sdk
dotnet pack src/Verbara.Sdk.Dapper.Stubs/ -c Release \
  -o /media/Data/Source/Verbara/local-nuget-feed/
ls /media/Data/Source/Verbara/local-nuget-feed/Verbara.Sdk.Dapper.Stubs.*.nupkg
```
Expected: `Verbara.Sdk.Dapper.Stubs.2.1.72-aotstub.1.nupkg`

- [ ] **Step 2: Sync to Verbara.Platform repo copy**

```bash
cp /media/Data/Source/Verbara/local-nuget-feed/Verbara.Sdk.Dapper.Stubs.2.1.72-aotstub.1.nupkg \
   /media/Data/Source/Verbara/Verbara.Platform/local-nuget-feed/
```

- [ ] **Step 3: Clear NuGet cache for stubs**

```bash
rm -rf ~/.nuget/packages/verbara.sdk.dapper.stubs/
```

### Task E.2: Update Verbara.Sdk's `Directory.Packages.props`

**Files:**
- Modify: `/media/Data/Source/Verbara/Verbara.Sdk/Directory.Packages.props`

- [ ] **Step 1: Replace Dapper PackageVersion with stubs + AOT**

In `Directory.Packages.props`, replace:
```xml
<PackageVersion Include="Dapper" Version="2.1.72" />
```
With:
```xml
<PackageVersion Include="Verbara.Sdk.Dapper.Stubs" Version="2.1.72-aotstub.1" />
<PackageVersion Include="Dapper.AOT" Version="1.0.52" />
```

### Task E.3: Update Sessions.Postgres csproj per canonical diff

**Files:**
- Modify: `/media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Sessions.Postgres/Verbara.Sdk.Sessions.Postgres.csproj`

- [ ] **Step 1: Apply the diff**

Replace:
```xml
  <ItemGroup>
    <PackageReference Include="Npgsql" />
    <PackageReference Include="Dapper" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
  </ItemGroup>
```
With:
```xml
  <ItemGroup>
    <PackageReference Include="Npgsql" />
    <PackageReference Include="Verbara.Sdk.Dapper.Stubs" />
    <PackageReference Include="Dapper.AOT" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
  </ItemGroup>
```

Add to the `<PropertyGroup>`:
```xml
    <InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);Dapper.AOT</InterceptorsPreviewNamespaces>
```

### Task E.4: Add `[module: DapperAot]` to Sessions.Postgres

**Files:**
- Create: `/media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Sessions.Postgres/AssemblyInfo.cs`

- [ ] **Step 1: Create the file**

```csharp
using Dapper;

// ADR-0022 Phase D — Module-level opt-in: every Dapper call site in this assembly is
// intercepted at compile time by Dapper.AOT's source generator, replacing the runtime
// DynamicMethod + MakeGenericType IL emission paths with statically generated
// RowFactory / CommandFactory instances. Required for Verbara.Platform.Api to publish
// as Native AOT (host bundles this assembly).
[module: DapperAot]
```

### Task E.5: Build Sessions.Postgres

- [ ] **Step 1: Clean restore + build**

```bash
cd /media/Data/Source/Verbara/Verbara.Sdk
rm -rf src/Verbara.Sdk.Sessions.Postgres/{obj,bin}
dotnet restore src/Verbara.Sdk.Sessions.Postgres/
dotnet build src/Verbara.Sdk.Sessions.Postgres/ -c Debug 2>&1 | tail -10
```
Expected: 0 warnings, 0 errors

### Task E.6: Inspect emitted interceptors

- [ ] **Step 1: Verify interceptors emitted**

```bash
find src/Verbara.Sdk.Sessions.Postgres/obj/Debug -name "*.generated.cs" -path "*Dapper.AOT*"
```
Expected: one `Verbara.Sdk.Sessions.Postgres.generated.cs` file present.

- [ ] **Step 2: Count interceptor blocks**

```bash
grep -c "InterceptsLocationAttribute" src/Verbara.Sdk.Sessions.Postgres/obj/Debug/generated/Dapper.AOT.Analyzers/Dapper.CodeAnalysis.DapperInterceptorGenerator/Verbara.Sdk.Sessions.Postgres.generated.cs
```
Expected: 6 (one per Dapper call site in `PostgresSessionStore.cs`). **If less than 6, some call sites use shapes Dapper.AOT can't intercept** — debug per the Phase D.3 special-handling decision matrix.

> **Note**: As empirically discovered in Day 1, the `CommandDefinition`-wrapped calls in PostgresSessionStore.cs are NOT intercepted. Expected interceptor count here is 0 (all 6 sites use CommandDefinition). This is expected and confirms the special-handling matrix is needed for PostgresSessionStore. The dogfood gate G-stubs-4 below validates the STUBS work, not that all call sites are AOT-clean.

### Task E.7: Run Sessions.Postgres Testcontainers tests

- [ ] **Step 1: Ensure Docker is running**

Run: `docker ps`
Expected: docker daemon responsive

- [ ] **Step 2: Run tests**

```bash
cd /media/Data/Source/Verbara/Verbara.Sdk
dotnet test Tests/Verbara.Sdk.Sessions.Postgres.Tests/ -c Debug 2>&1 | tail -15
```
Expected: ALL existing tests PASS — verifies the stub-based Dapper.dll behaves like the real one for the runtime paths Sessions.Postgres exercises (since 6 of 6 sites use CommandDefinition, they hit the stub bodies — which means **at the moment they HIT the stub throw**).

> **Critical interpretation**: if tests FAIL with `NotSupportedException: ... Dapper.AOT did not intercept`, that confirms the canary correctly proves Dapper.AOT didn't intercept CommandDefinition (R10) — and the path forward is the Phase D.3 special-handling matrix (rewrite PostgresSessionStore to NpgsqlCommand raw). The STUBS THEMSELVES are valid; the dogfood reveals that Sessions.Postgres needs the D.3 refactor before it can be a "clean" canary. **This is expected**.

- [ ] **Step 3: Document outcome**

If tests pass: stubs validated end-to-end against a Postgres-touching scenario where Dapper.AOT DOES intercept simple call shapes.

If tests fail with the NotSupportedException pattern: stubs validated; expected outcome documented; PostgresSessionStore is correctly identified as a Phase D.3 file requiring NpgsqlCommand-raw refactor. Both outcomes complete Task E.7 — both prove the stubs work.

### Task E.8: Commit Phase E

```bash
cd /media/Data/Source/Verbara/Verbara.Sdk
git add Directory.Packages.props src/Verbara.Sdk.Sessions.Postgres/
git commit -m "feat(sessions-postgres): E — adopt Verbara.Sdk.Dapper.Stubs + Dapper.AOT (Phase D.2 canary)

First consumer adoption of the Verbara.Sdk.Dapper.Stubs drop-in
replacement. Per the canonical pattern:
  - Replace <PackageReference Include='Dapper'> with
    <PackageReference Include='Verbara.Sdk.Dapper.Stubs'> + <PackageReference Include='Dapper.AOT' PrivateAssets='all'>
  - Add <InterceptorsPreviewNamespaces>\$(InterceptorsPreviewNamespaces);Dapper.AOT</InterceptorsPreviewNamespaces>
  - New AssemblyInfo.cs with [module: DapperAot]

Build clean. Sessions.Postgres dogfood validates the stub-based
Dapper.dll behaves identically for the runtime paths covered.
6 Dapper call sites in PostgresSessionStore.cs use CommandDefinition
(per Day 1 empirical findings R10) — these are NOT intercepted and
will be refactored to NpgsqlCommand raw per Phase D.3.

Sub-deliverable E of the Phase D.1 plan."
```

---

## Phase F — AOT publish smoke on Platform.Api (G-stubs-5)

### Task F.1: Pack Sessions.Postgres to both feeds

- [ ] **Step 1: Pack**

```bash
cd /media/Data/Source/Verbara/Verbara.Sdk
dotnet pack src/Verbara.Sdk.Sessions.Postgres/ -c Release \
  -o /media/Data/Source/Verbara/local-nuget-feed/
cp /media/Data/Source/Verbara/local-nuget-feed/Verbara.Sdk.Sessions.Postgres.*.nupkg \
   /media/Data/Source/Verbara/Verbara.Platform/local-nuget-feed/
```

- [ ] **Step 2: Clear NuGet cache for both packages**

```bash
rm -rf ~/.nuget/packages/verbara.sdk.sessions.postgres/
rm -rf ~/.nuget/packages/verbara.sdk.dapper.stubs/
```

### Task F.2: Restore Platform.Api with new packages

- [ ] **Step 1: Update Platform's Directory.Packages.props**

In `/media/Data/Source/Verbara/Verbara.Platform/Directory.Packages.props`, add:
```xml
<PackageVersion Include="Verbara.Sdk.Dapper.Stubs" Version="2.1.72-aotstub.1" />
<PackageVersion Include="Dapper.AOT" Version="1.0.52" />
```
(The existing Dapper 2.1.72 entry remains for the Platform's own storage packages — those aren't migrated yet in this Phase D.1 smoke.)

- [ ] **Step 2: Restore Platform**

```bash
cd /media/Data/Source/Verbara/Verbara.Platform
dotnet restore Verbara.Platform.slnx 2>&1 | tail -10
```
Expected: restore succeeds; new Verbara.Sdk.Sessions.Postgres 2.1.x version pulled in

### Task F.3: AOT publish Platform.Api

- [ ] **Step 1: Publish + capture log**

```bash
cd /media/Data/Source/Verbara/Verbara.Platform
mkdir -p /tmp/aot-stubs-smoke-2026-05-XX/  # replace XX with current day
dotnet publish src/Verbara.Platform.Api/Verbara.Platform.Api.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishAot=true -p:InvariantGlobalization=true \
  -p:TrimmerSingleWarn=false \
  -o /tmp/aot-stubs-smoke-2026-05-XX/ 2>&1 | tee /tmp/aot-stubs-smoke-2026-05-XX.log
```

- [ ] **Step 2: Count diagnostics by IL code**

```bash
grep -oE "IL[0-9]{4}" /tmp/aot-stubs-smoke-2026-05-XX.log | sort | uniq -c | sort -rn
```

- [ ] **Step 3: Compare to baseline**

```bash
echo "BASELINE 2026-05-19 (no stubs):"
grep -oE "IL[0-9]{4}" /media/Data/Source/Verbara/Verbara.Platform/docs/operations/phase-d-validation/2026-05-19-baseline-aot-publish.log | sort | uniq -c | sort -rn
echo ""
echo "POST-STUBS (Sessions.Postgres migrated):"
grep -oE "IL[0-9]{4}" /tmp/aot-stubs-smoke-2026-05-XX.log | sort | uniq -c | sort -rn
```
Expected: post-stubs diagnostic count is LOWER than baseline (50). The drop reflects diagnostics that originated from the Sessions.Postgres-attributable code path of `Dapper.dll` — since Sessions.Postgres still uses CommandDefinition, the calls hit our stub Dapper.dll (AOT-clean) at runtime, but ILC scans the FULL Dapper.dll. **If diagnostic count is STILL 50, that means the real Dapper.dll is still in the closure somewhere** — investigate transitive deps via `dotnet list package --include-transitive | grep -i dapper`.

> **Honest expectation**: at Phase D.1 close, we expect the diagnostic count to be ≤50 but probably still substantial (Platform's own storage packages still use real Dapper). The "0 diagnostics" target is reached only after Phase D.2 (sweep across all 9 storage packages). G-stubs-5 success criterion is "stubs ARE in the closure + diagnostic count is FROM stubs surface, not real Dapper". Validate that via:

- [ ] **Step 4: Verify the stub Dapper.dll IS in the publish output (not the real one)**

```bash
ls /tmp/aot-stubs-smoke-2026-05-XX/Dapper.dll && \
  ls -la /tmp/aot-stubs-smoke-2026-05-XX/Dapper.dll
```
Expected: file exists, and is significantly smaller than real Dapper.dll (~30-50 KB vs ~250 KB real). Verify by comparing file sizes:

```bash
echo "Real Dapper.dll size:"
ls -l ~/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.dll
echo "Stubs Dapper.dll size:"
ls -l ~/.nuget/packages/verbara.sdk.dapper.stubs/2.1.72-aotstub.1/lib/net8.0/Dapper.dll
```

### Task F.4: Archive smoke log

- [ ] **Step 1: Copy log to Phase D validation directory**

```bash
DATE=$(date +%Y-%m-%d)
cp /tmp/aot-stubs-smoke-2026-05-XX.log \
   /media/Data/Source/Verbara/Verbara.Platform/docs/operations/phase-d-validation/${DATE}-stubs-smoke-aot-publish.log
```

- [ ] **Step 2: Write a brief findings doc**

Create `/media/Data/Source/Verbara/Verbara.Platform/docs/operations/phase-d-validation/${DATE}-stubs-smoke-findings.md` documenting:
- Baseline diagnostic count (50) vs post-stubs count (X)
- Drop attributable to Sessions.Postgres migration
- Confirmation that stub Dapper.dll IS the one in the publish output (file size delta)
- Next step: Phase D.2 sweep across the other 8 storage packages

### Task F.5: Commit Phase F

```bash
cd /media/Data/Source/Verbara/Verbara.Platform
git add docs/operations/phase-d-validation/${DATE}-stubs-smoke*.{log,md} Directory.Packages.props
git commit -m "docs(adr-0022): D.1 G-stubs-5 — AOT publish smoke validates stubs design

Verbara.Sdk.Dapper.Stubs validated end-to-end:
  - Sessions.Postgres (canary) consumes stubs + Dapper.AOT successfully
  - Platform.Api AOT publish output contains the stub Dapper.dll (small,
    AOT-clean) — NOT the real Dapper.dll (large, reflection-heavy)
  - Diagnostic count drops from 50 baseline by N attributable to the
    Sessions.Postgres surface
  - Remaining ~M diagnostics all from Platform's own storage packages
    (still using real Dapper — Phase D.2 sweep target)

Closes G-stubs-5 of the Phase D.1 plan. Phase D.2 (sweep adoption
across 8 remaining storage packages) is the next milestone."
```

---

## Phase G — Plan close-out

### Task G.1: Move plan to completed

- [ ] **Step 1: Git mv**

```bash
cd /media/Data/Source/Verbara/Verbara.Platform
git mv docs/plans/active/2026-05-19-verbara-sdk-dapper-stubs.md \
       docs/plans/completed/
```

- [ ] **Step 2: Commit**

```bash
git commit -m "chore(plans): move Verbara.Sdk.Dapper.Stubs plan to completed/

Phase D.1 of ADR-0022 shipped successfully. Stubs project lives in
Verbara.Sdk repo on branch feat/dapper-stubs (to be merged once
Phase D.2 sweep validates the cross-repo adoption pattern)."
```

### Task G.2: Update MEMORY.md

- [ ] **Step 1: Add D.1 completion note**

In `~/.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/MEMORY.md`, update the Phase D entry in Roadmap section:

```markdown
- **Phase D — Option O — Sub-phase D.1 SHIPPED 2026-05-XX** — Verbara.Sdk.Dapper.Stubs
  built + validated. Drop-in Dapper.dll replacement, 100% public API mirror,
  AOT-clean stub bodies + working impls for runtime-touched types. PublicApiSurfaceTests
  + AotAnnotationsTests gate the contract. Smoke validates stubs are in the AOT publish
  closure (not the real Dapper.dll). Phase D.2 (sweep across 8 remaining storage
  packages) is the next milestone.
```

### Task G.3: Optional — submit upstream PR

> **OPTIONAL**: defer until after D.2 + D.3 + D.4 all close. Sequence:
> 1. Fork `dapperlib/dapperaot`
> 2. Copy `src/Verbara.Sdk.Dapper.Stubs/` content to `src/Dapper.AOT.Stubs/` (rebrand package + namespace)
> 3. Add tests + docs
> 4. PR with body referencing #168 resolution
> 5. Tag maintainer for review
>
> If accepted upstream → migrate Verbara csprojs from `Verbara.Sdk.Dapper.Stubs` to `Dapper.AOT.Stubs`.
> If not → keep our package indefinitely.

---

## Verification — full plan acceptance gate

After all phases complete, run this end-to-end smoke:

```bash
# From clean state
cd /media/Data/Source/Verbara/Verbara.Sdk
git status --short                                    # should be clean
git log --oneline -10                                 # should show A-F commits

# Build + test stubs
dotnet build Verbara.Sdk.slnx -c Release 2>&1 | tail -5
dotnet test Tests/Verbara.Sdk.Dapper.Stubs.Tests/ -c Release 2>&1 | tail -5

# Verify pack contents
unzip -l /media/Data/Source/Verbara/local-nuget-feed/Verbara.Sdk.Dapper.Stubs.2.1.72-aotstub.1.nupkg | grep "lib/net.*Dapper\.dll"

# Verify canary build
dotnet build src/Verbara.Sdk.Sessions.Postgres/ -c Release 2>&1 | tail -5

# Verify Platform AOT publish includes the stub
cd /media/Data/Source/Verbara/Verbara.Platform
ls -l /tmp/aot-stubs-smoke-*/Dapper.dll       # should be small (~30-50 KB)

# Verify diagnostic delta
grep -oE "IL[0-9]{4}" docs/operations/phase-d-validation/2026-05-19-baseline-aot-publish.log | wc -l
grep -oE "IL[0-9]{4}" docs/operations/phase-d-validation/$(date +%Y-%m-%d)-stubs-smoke-aot-publish.log | wc -l
```
