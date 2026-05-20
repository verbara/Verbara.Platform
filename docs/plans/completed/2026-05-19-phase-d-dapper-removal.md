# Phase D — Total Dapper Removal (Foundation + Pilot) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the owned `Verbara.Sdk.Data.Npgsql` micro-layer, prove it on the SDK Sessions.Postgres canary, then migrate `Verbara.Platform.Storage.Postgres` (the first package in Platform.Api's AOT closure) off Dapper — and empirically confirm the AOT diagnostic count drops from 50.

**Architecture:** Replace Dapper's two reflection halves (anonymous-object param binding + reader→object mapping) with a fully-owned, AOT-clean micro-layer: an `NpgsqlExecutor` facade (centralizes connection/command/dispose plumbing once) + reflection-free name-based `NpgsqlDataReader` helpers + hand-written `Map()` per `*Row` class + explicit `NpgsqlParameter` binding. No source generator, no third-party, no Dapper, no Dapper.AOT.

**Tech Stack:** .NET 10, Npgsql 10.0.2, xunit 2.9.3 + FluentAssertions 7.1.0, Testcontainers.PostgreSql for integration tests. `TreatWarningsAsErrors=true`, `WarningLevel=9999`, `Nullable=enable`, AOT analyzers ON.

**Spec:** [`docs/specs/2026-05-19-phase-d-dapper-removal-raw-npgsql-design.md`](../../specs/2026-05-19-phase-d-dapper-removal-raw-npgsql-design.md)
**Branch:** `feat/phase-d-dapper-removal` (Verbara.Platform); SDK work on its own `feat/dapper-removal` branch in the SDK repo.

---

## Scope of THIS plan

This plan covers **Phase 0 (pre-flight) + Phase 1 (foundation package) + Phase 1b (SDK canary) + Phase 2 (Platform pilot + AOT-delta gate)**. This is a self-contained, working, testable deliverable: the micro-layer exists, two storage packages are Dapper-free, and the proof-of-concept AOT gate has run.

**Phases 3–6 (Pro 7-package sweep, Platform.Api AOT flip + triple gate, image cutover, 24h soak) get their own implementation plan, written after Phase 2's gate validates the approach** — because the exact sweep playbook depends on what the pilot teaches. A roadmap stub for them is at the end of this file.

> **Cross-repo dev loop** (run after every SDK/Pro pack — `feedback_nuget_two_feeds.md`):
> ```sh
> dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/
> cp /media/Data/Source/Verbara/local-nuget-feed/<pkg>.nupkg /media/Data/Source/Verbara/Verbara.Platform/local-nuget-feed/
> rm -rf ~/.nuget/packages/verbara.sdk.data.npgsql/   # or the package being refreshed
> cd <consuming repo> && dotnet restore
> ```

## File Structure

**SDK repo (`/media/Data/Source/Verbara/Verbara.Sdk/`) — NEW package:**
- `src/Verbara.Sdk.Data.Npgsql/Verbara.Sdk.Data.Npgsql.csproj` — package metadata, AOT-clean, references Npgsql only
- `src/Verbara.Sdk.Data.Npgsql/NpgsqlReaderExtensions.cs` — reflection-free name-based reader getters
- `src/Verbara.Sdk.Data.Npgsql/NpgsqlExecutor.cs` — executor facade (DataSource + connection/transaction overloads)
- `tests/Verbara.Sdk.Data.Npgsql.Tests/Verbara.Sdk.Data.Npgsql.Tests.csproj`
- `tests/Verbara.Sdk.Data.Npgsql.Tests/PostgresFixture.cs` — Testcontainers fixture
- `tests/Verbara.Sdk.Data.Npgsql.Tests/NpgsqlReaderExtensionsTests.cs`
- `tests/Verbara.Sdk.Data.Npgsql.Tests/NpgsqlExecutorTests.cs`

**SDK repo — migrate (canary):**
- `src/Verbara.Sdk.Sessions.Postgres/PostgresSessionStore.cs` — remove Dapper; use facade
- `src/Verbara.Sdk.Sessions.Postgres/Verbara.Sdk.Sessions.Postgres.csproj` — drop stubs/Dapper.AOT, add Data.Npgsql
- `Verbara.Sdk/Directory.Packages.props` — drop `Verbara.Sdk.Dapper.Stubs` + `Dapper.AOT`; add `Verbara.Sdk.Data.Npgsql`

**Platform repo (`/media/Data/Source/Verbara/Verbara.Platform/`) — pilot:**
- `src/Verbara.Platform.Storage.Postgres/Stores/*.cs` — ~30 stores, 55 `*Row` classes
- `src/Verbara.Platform.Storage.Postgres/Verbara.Platform.Storage.Postgres.csproj`
- `src/Verbara.Platform.Identity/DataProtection/DapperXmlRepository.cs`
- `Verbara.Platform/Directory.Packages.props` — drop `Dapper`/`Dapper.AOT`/`Verbara.Sdk.Dapper.Stubs`; add `Verbara.Sdk.Data.Npgsql`

---

## Phase 0 — Pre-flight

### Task 0.1: Capture green-test + AOT-diagnostic baseline

**Files:** none (measurement only)

- [ ] **Step 1: Confirm SDK + Platform tests green before any change**

Run:
```sh
cd /media/Data/Source/Verbara/Verbara.Sdk && dotnet test -c Release 2>&1 | tail -5
cd /media/Data/Source/Verbara/Verbara.Platform && dotnet test Verbara.Platform.slnx -c Release 2>&1 | tail -5
```
Expected: all green. Record counts (SDK ~3,079; Platform.Api 943 + Realtime 22).

- [ ] **Step 2: Capture the baseline AOT diagnostic count**

Run:
```sh
cd /media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api
dotnet publish Verbara.Platform.Api.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishAot=true -p:InvariantGlobalization=true -p:TrimmerSingleWarn=false \
  -o /tmp/aot-baseline/ 2>&1 | tee /tmp/aot-baseline.log || true
grep -cE "IL2026|IL3050|IL2046|IL2060|IL2067|IL2070|IL2075|IL2080" /tmp/aot-baseline.log
```
Expected: ~50 (matches the documented Day-0 baseline). This is the number Phase 2 must move.

- [ ] **Step 3: Create the SDK working branch**

Run:
```sh
cd /media/Data/Source/Verbara/Verbara.Sdk && git checkout -b feat/dapper-removal
```
Expected: switched to new branch.

---

## Phase 1 — Foundation package `Verbara.Sdk.Data.Npgsql`

### Task 1.1: Scaffold the package

**Files:**
- Create: `src/Verbara.Sdk.Data.Npgsql/Verbara.Sdk.Data.Npgsql.csproj`
- Modify: `Verbara.Sdk/Directory.Packages.props` (no change yet — Npgsql version already present at line 38)
- Modify: `Verbara.Sdk/Verbara.Sdk.slnx` (add project)

- [ ] **Step 1: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>Verbara.Sdk.Data.Npgsql - AOT-clean micro-layer over raw Npgsql ADO.NET: NpgsqlExecutor facade + reflection-free NpgsqlDataReader helpers. Replaces Dapper for Native AOT publishing. No reflection, no source generators.</Description>
    <PackageTags>npgsql;postgresql;ado-net;aot;data-access;sdk</PackageTags>
    <EnablePackageValidation>false</EnablePackageValidation>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Npgsql" />
  </ItemGroup>
</Project>
```

(Common props — `IsAotCompatible=true`, `Nullable=enable`, `TreatWarningsAsErrors=true`, target framework, AOT analyzers — are inherited from the SDK `Directory.Build.props`. Verify they are by reading it; if `IsAotCompatible` is not the default, add `<IsAotCompatible>true</IsAotCompatible>` + `<EnableAotAnalyzer>true</EnableAotAnalyzer>` to the PropertyGroup.)

- [ ] **Step 2: Add the project to the solution**

Run:
```sh
cd /media/Data/Source/Verbara/Verbara.Sdk
dotnet sln Verbara.Sdk.slnx add src/Verbara.Sdk.Data.Npgsql/Verbara.Sdk.Data.Npgsql.csproj
```
Expected: "Project ... added to the solution."

- [ ] **Step 3: Verify it builds empty**

Run: `dotnet build src/Verbara.Sdk.Data.Npgsql/Verbara.Sdk.Data.Npgsql.csproj -c Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Commit**

```sh
git add src/Verbara.Sdk.Data.Npgsql/ Verbara.Sdk.slnx
git commit -m "feat(data-npgsql): scaffold Verbara.Sdk.Data.Npgsql package"
```

### Task 1.2: `NpgsqlReaderExtensions` (reflection-free reader getters)

**Files:**
- Create: `src/Verbara.Sdk.Data.Npgsql/NpgsqlReaderExtensions.cs`
- Create: `tests/Verbara.Sdk.Data.Npgsql.Tests/Verbara.Sdk.Data.Npgsql.Tests.csproj`
- Create: `tests/Verbara.Sdk.Data.Npgsql.Tests/PostgresFixture.cs`
- Create: `tests/Verbara.Sdk.Data.Npgsql.Tests/NpgsqlReaderExtensionsTests.cs`

- [ ] **Step 1: Create the test project csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsAotCompatible>false</IsAotCompatible>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Testcontainers.PostgreSql" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Verbara.Sdk.Data.Npgsql\Verbara.Sdk.Data.Npgsql.csproj" />
  </ItemGroup>
</Project>
```

Run: `dotnet sln Verbara.Sdk.slnx add tests/Verbara.Sdk.Data.Npgsql.Tests/Verbara.Sdk.Data.Npgsql.Tests.csproj`
(If `Testcontainers.PostgreSql` is not yet in `Directory.Packages.props`, copy the `<PackageVersion>` line from an existing `*.Postgres.Tests.csproj` — e.g. `Verbara.Sdk.Sessions.Postgres.Tests`.)

- [ ] **Step 2: Create the Testcontainers fixture**

```csharp
using Testcontainers.PostgreSql;
using Xunit;

namespace Verbara.Sdk.Data.Npgsql.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder().WithImage("postgres:18-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
```

- [ ] **Step 3: Write the failing test for the reader getters**

```csharp
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Verbara.Sdk.Data.Npgsql.Tests;

[Collection("postgres")]
public sealed class NpgsqlReaderExtensionsTests
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlReaderExtensionsTests(PostgresFixture fx)
        => _dataSource = NpgsqlDataSource.Create(fx.ConnectionString);

    [Fact]
    public async Task GetGetters_ShouldReadTypedValues_WhenColumnsPresent()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using (var ddl = new NpgsqlCommand(
            "CREATE TEMP TABLE t (s text, n int, b boolean, ts timestamptz, g uuid, d date, nn int)", conn))
            await ddl.ExecuteNonQueryAsync();
        var g = Guid.NewGuid();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO t VALUES ('hi', 42, true, '2026-05-19T10:00:00Z', @g, '2026-05-19', NULL)", conn))
        {
            ins.Parameters.Add(new NpgsqlParameter("g", g));
            await ins.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand("SELECT s, n, b, ts, g, d, nn FROM t", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        (await r.ReadAsync()).Should().BeTrue();

        r.GetString("s").Should().Be("hi");
        r.GetInt32("n").Should().Be(42);
        r.GetBoolean("b").Should().BeTrue();
        r.GetDateTimeOffset("ts").Should().Be(new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.Zero));
        r.GetGuid("g").Should().Be(g);
        r.GetDateOnly("d").Should().Be(new DateOnly(2026, 5, 19));
        r.GetInt32OrNull("nn").Should().BeNull();
        r.GetStringOrNull("s").Should().Be("hi");
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/Verbara.Sdk.Data.Npgsql.Tests -c Release --filter NpgsqlReaderExtensionsTests`
Expected: FAIL — `GetString(string)` / `GetInt32(string)` etc. do not exist (compile error).

- [ ] **Step 5: Implement `NpgsqlReaderExtensions`**

```csharp
using Npgsql;

namespace Verbara.Sdk.Data.Npgsql;

/// <summary>
/// Reflection-free, name-based typed getters over <see cref="NpgsqlDataReader"/>.
/// AOT-safe replacement for Dapper's reader-to-object materialization. Each getter
/// resolves the column ordinal by name and reads the value with the strongly-typed
/// Npgsql accessor; <c>*OrNull</c> variants null-check first.
/// </summary>
public static class NpgsqlReaderExtensions
{
    public static string GetString(this NpgsqlDataReader r, string column)
        => r.GetString(r.GetOrdinal(column));

    public static string? GetStringOrNull(this NpgsqlDataReader r, string column)
    {
        var o = r.GetOrdinal(column);
        return r.IsDBNull(o) ? null : r.GetString(o);
    }

    public static int GetInt32(this NpgsqlDataReader r, string column)
        => r.GetInt32(r.GetOrdinal(column));

    public static int? GetInt32OrNull(this NpgsqlDataReader r, string column)
    {
        var o = r.GetOrdinal(column);
        return r.IsDBNull(o) ? null : r.GetInt32(o);
    }

    public static long GetInt64(this NpgsqlDataReader r, string column)
        => r.GetInt64(r.GetOrdinal(column));

    public static long? GetInt64OrNull(this NpgsqlDataReader r, string column)
    {
        var o = r.GetOrdinal(column);
        return r.IsDBNull(o) ? null : r.GetInt64(o);
    }

    public static short GetInt16(this NpgsqlDataReader r, string column)
        => r.GetInt16(r.GetOrdinal(column));

    public static bool GetBoolean(this NpgsqlDataReader r, string column)
        => r.GetBoolean(r.GetOrdinal(column));

    public static bool? GetBooleanOrNull(this NpgsqlDataReader r, string column)
    {
        var o = r.GetOrdinal(column);
        return r.IsDBNull(o) ? null : r.GetBoolean(o);
    }

    public static DateTime GetDateTime(this NpgsqlDataReader r, string column)
        => r.GetDateTime(r.GetOrdinal(column));

    public static DateTime? GetDateTimeOrNull(this NpgsqlDataReader r, string column)
    {
        var o = r.GetOrdinal(column);
        return r.IsDBNull(o) ? null : r.GetDateTime(o);
    }

    public static DateTimeOffset GetDateTimeOffset(this NpgsqlDataReader r, string column)
        => r.GetFieldValue<DateTimeOffset>(r.GetOrdinal(column));

    public static DateTimeOffset? GetDateTimeOffsetOrNull(this NpgsqlDataReader r, string column)
    {
        var o = r.GetOrdinal(column);
        return r.IsDBNull(o) ? null : r.GetFieldValue<DateTimeOffset>(o);
    }

    public static Guid GetGuid(this NpgsqlDataReader r, string column)
        => r.GetGuid(r.GetOrdinal(column));

    public static DateOnly GetDateOnly(this NpgsqlDataReader r, string column)
        => r.GetFieldValue<DateOnly>(r.GetOrdinal(column));

    public static decimal GetDecimal(this NpgsqlDataReader r, string column)
        => r.GetDecimal(r.GetOrdinal(column));

    public static double GetDouble(this NpgsqlDataReader r, string column)
        => r.GetDouble(r.GetOrdinal(column));
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/Verbara.Sdk.Data.Npgsql.Tests -c Release --filter NpgsqlReaderExtensionsTests`
Expected: PASS (1 test).

- [ ] **Step 7: Commit**

```sh
git add src/Verbara.Sdk.Data.Npgsql/NpgsqlReaderExtensions.cs tests/Verbara.Sdk.Data.Npgsql.Tests/
git commit -m "feat(data-npgsql): reflection-free name-based NpgsqlDataReader getters"
```

### Task 1.3: `NpgsqlExecutor` — DataSource-level facade

**Files:**
- Create: `src/Verbara.Sdk.Data.Npgsql/NpgsqlExecutor.cs`
- Create: `tests/Verbara.Sdk.Data.Npgsql.Tests/NpgsqlExecutorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Verbara.Sdk.Data.Npgsql.Tests;

[Collection("postgres")]
public sealed class NpgsqlExecutorTests
{
    private readonly NpgsqlDataSource _ds;

    public NpgsqlExecutorTests(PostgresFixture fx)
        => _ds = NpgsqlDataSource.Create(fx.ConnectionString);

    private sealed record Item(int Id, string Name);

    private static Item Map(NpgsqlDataReader r) => new(r.GetInt32("id"), r.GetString("name"));

    [Fact]
    public async Task Execute_Query_Scalar_ShouldRoundTrip()
    {
        await _ds.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS items (id int primary key, name text)",
            static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM items", static _ => { }, CancellationToken.None);

        var rows = await _ds.ExecuteAsync(
            "INSERT INTO items (id, name) VALUES (@Id, @Name)",
            p => { p.Add(new NpgsqlParameter("Id", 1)); p.Add(new NpgsqlParameter("Name", "alpha")); },
            CancellationToken.None);
        rows.Should().Be(1);

        var single = await _ds.QuerySingleOrDefaultAsync(
            "SELECT id, name FROM items WHERE id = @Id",
            p => p.Add(new NpgsqlParameter("Id", 1)), Map, CancellationToken.None);
        single.Should().Be(new Item(1, "alpha"));

        var missing = await _ds.QuerySingleOrDefaultAsync(
            "SELECT id, name FROM items WHERE id = @Id",
            p => p.Add(new NpgsqlParameter("Id", 999)), Map, CancellationToken.None);
        missing.Should().BeNull();

        var list = await _ds.QueryListAsync(
            "SELECT id, name FROM items ORDER BY id", static _ => { }, Map, CancellationToken.None);
        list.Should().ContainSingle().Which.Should().Be(new Item(1, "alpha"));

        var count = await _ds.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM items", static _ => { }, CancellationToken.None);
        count.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Verbara.Sdk.Data.Npgsql.Tests -c Release --filter NpgsqlExecutorTests`
Expected: FAIL — `ExecuteAsync` / `QuerySingleOrDefaultAsync` / `QueryListAsync` / `ExecuteScalarAsync` not defined.

- [ ] **Step 3: Implement the DataSource-level facade**

```csharp
using Npgsql;

namespace Verbara.Sdk.Data.Npgsql;

/// <summary>
/// AOT-clean execution facade over <see cref="NpgsqlDataSource"/>. Centralizes
/// connection open/dispose, command creation, parameter binding, and reader
/// iteration that Dapper used to provide. Callers supply a <paramref name="bind"/>
/// delegate to add parameters and a <c>map</c> delegate to materialize rows;
/// both are explicit and reflection-free.
/// </summary>
public static class NpgsqlExecutor
{
    public static async Task<int> ExecuteAsync(
        this NpgsqlDataSource dataSource, string sql,
        Action<NpgsqlParameterCollection> bind, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        bind(cmd.Parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ExecuteScalarAsync<T>(
        this NpgsqlDataSource dataSource, string sql,
        Action<NpgsqlParameterCollection> bind, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        bind(cmd.Parameters);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? default : (T)result;
    }

    public static async Task<T?> QuerySingleOrDefaultAsync<T>(
        this NpgsqlDataSource dataSource, string sql,
        Action<NpgsqlParameterCollection> bind, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        bind(cmd.Parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return default;
        return map(reader);
    }

    public static async Task<List<T>> QueryListAsync<T>(
        this NpgsqlDataSource dataSource, string sql,
        Action<NpgsqlParameterCollection> bind, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        bind(cmd.Parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<T>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(map(reader));
        return results;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Verbara.Sdk.Data.Npgsql.Tests -c Release --filter NpgsqlExecutorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```sh
git add src/Verbara.Sdk.Data.Npgsql/NpgsqlExecutor.cs tests/Verbara.Sdk.Data.Npgsql.Tests/NpgsqlExecutorTests.cs
git commit -m "feat(data-npgsql): NpgsqlExecutor DataSource-level facade"
```

### Task 1.4: `NpgsqlExecutor` — connection/transaction overloads

**Files:**
- Modify: `src/Verbara.Sdk.Data.Npgsql/NpgsqlExecutor.cs`
- Modify: `tests/Verbara.Sdk.Data.Npgsql.Tests/NpgsqlExecutorTests.cs`

- [ ] **Step 1: Add the failing transaction test**

Add to `NpgsqlExecutorTests`:
```csharp
    [Fact]
    public async Task ExecuteOnConnection_ShouldHonorTransaction_WhenRolledBack()
    {
        await _ds.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS tx_items (id int primary key)", static _ => { }, CancellationToken.None);
        await _ds.ExecuteAsync("DELETE FROM tx_items", static _ => { }, CancellationToken.None);

        await using (var conn = await _ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync("INSERT INTO tx_items (id) VALUES (@Id)",
                p => p.Add(new NpgsqlParameter("Id", 7)), tx, CancellationToken.None);
            await tx.RollbackAsync();
        }

        var count = await _ds.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tx_items", static _ => { }, CancellationToken.None);
        count.Should().Be(0);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Verbara.Sdk.Data.Npgsql.Tests -c Release --filter ExecuteOnConnection_ShouldHonorTransaction`
Expected: FAIL — connection-level `ExecuteAsync(this NpgsqlConnection, ...)` not defined.

- [ ] **Step 3: Add the connection-level overload**

Append to `NpgsqlExecutor`:
```csharp
    public static async Task<int> ExecuteAsync(
        this NpgsqlConnection connection, string sql,
        Action<NpgsqlParameterCollection> bind, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        bind(cmd.Parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Verbara.Sdk.Data.Npgsql.Tests -c Release`
Expected: PASS (all executor + reader tests).

- [ ] **Step 5: Commit**

```sh
git add src/Verbara.Sdk.Data.Npgsql/NpgsqlExecutor.cs tests/Verbara.Sdk.Data.Npgsql.Tests/NpgsqlExecutorTests.cs
git commit -m "feat(data-npgsql): connection-level ExecuteAsync transaction overload"
```

### Task 1.5: Pack the foundation package to both feeds

**Files:** none

- [ ] **Step 1: Pack**

Run:
```sh
cd /media/Data/Source/Verbara/Verbara.Sdk
dotnet pack src/Verbara.Sdk.Data.Npgsql/Verbara.Sdk.Data.Npgsql.csproj -c Release \
  -o /media/Data/Source/Verbara/local-nuget-feed/
cp /media/Data/Source/Verbara/local-nuget-feed/Verbara.Sdk.Data.Npgsql.*.nupkg \
   /media/Data/Source/Verbara/Verbara.Platform/local-nuget-feed/
```
Expected: `.nupkg` in both feeds. Record the version (e.g. `2.1.3` per SDK versioning).

---

## Phase 1b — SDK canary: migrate `Verbara.Sdk.Sessions.Postgres`

> This reverts the D.1 stubs/Dapper.AOT canary and proves the facade handles JSONB, transactions, and the `ICustomQueryParameter` JSONB case on a real store.

### Task 1b.1: Swap package references off Dapper

**Files:**
- Modify: `src/Verbara.Sdk.Sessions.Postgres/Verbara.Sdk.Sessions.Postgres.csproj`
- Modify: `Verbara.Sdk/Directory.Packages.props`

- [ ] **Step 1: Edit the Sessions.Postgres csproj**

Remove the `<InterceptorsPreviewNamespaces>` line, the entire Phase D canary comment block, and `<NoWarn>$(NoWarn);IL2026;IL3050;NU5104</NoWarn>`. In the `<ItemGroup>`, remove `Verbara.Sdk.Dapper.Stubs` and `Dapper.AOT`; add `Verbara.Sdk.Data.Npgsql`. Result:
```xml
  <ItemGroup>
    <PackageReference Include="Npgsql" />
    <PackageReference Include="Verbara.Sdk.Data.Npgsql" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
  </ItemGroup>
```
Also update the `<Description>` to drop "Dapper parameterized SQL" → "raw Npgsql parameterized SQL".

- [ ] **Step 2: Add the package version to SDK Directory.Packages.props**

In `Verbara.Sdk/Directory.Packages.props`, add (next to the Npgsql line at 38):
```xml
    <PackageVersion Include="Verbara.Sdk.Data.Npgsql" Version="2.1.3" />
```
(Match the version packed in Task 1.5. Do NOT remove the `Verbara.Sdk.Dapper.Stubs` / `Dapper.AOT` lines yet — Task 1b.4 removes them once nothing references them.)

- [ ] **Step 3: Restore (do not build yet — code still references Dapper)**

Run: `cd /media/Data/Source/Verbara/Verbara.Sdk && dotnet restore`
Expected: restore succeeds; build will fail until Task 1b.2 (expected).

### Task 1b.2: Migrate `PostgresSessionStore.cs` to the facade

**Files:**
- Modify: `src/Verbara.Sdk.Sessions.Postgres/PostgresSessionStore.cs`

- [ ] **Step 1: Replace the using + remove Dapper constructs**

Change the `using` block: remove `using Dapper;`; add `using Verbara.Sdk.Data.Npgsql;`. Keep `using NpgsqlTypes;` (needed for `NpgsqlDbType.Jsonb`). Delete the `JsonbParameter : SqlMapper.ICustomQueryParameter` nested class (lines 225–243) and the `BuildSaveParameters` method that returns `DynamicParameters` (lines 104–123) — both are replaced below.

- [ ] **Step 2: Add a private explicit bind helper (replaces BuildSaveParameters)**

```csharp
    private static void BindSaveParameters(
        NpgsqlParameterCollection p, CallSession session, CallSessionSnapshot snapshot, string json)
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? completedAt = IsTerminal(snapshot.State)
            ? snapshot.CompletedAt ?? now
            : null;

        p.Add(new NpgsqlParameter("session_id", session.SessionId));
        p.Add(new NpgsqlParameter("linked_id", (object?)snapshot.LinkedId ?? DBNull.Value));
        p.Add(new NpgsqlParameter("server_id", (object?)snapshot.ServerId ?? DBNull.Value));
        p.Add(new NpgsqlParameter("state", (short)snapshot.State));
        p.Add(new NpgsqlParameter("direction", (short)snapshot.Direction));
        p.Add(new NpgsqlParameter("created_at", snapshot.CreatedAt));
        p.Add(new NpgsqlParameter("updated_at", now));
        p.Add(new NpgsqlParameter("completed_at", (object?)completedAt ?? DBNull.Value));
        p.Add(new NpgsqlParameter("snapshot", NpgsqlDbType.Jsonb) { Value = json });
    }
```

- [ ] **Step 3: Rewrite the five query methods to the facade**

```csharp
    public override async ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = CallSessionSnapshot.FromSession(session);
        var json = Serialize(snapshot);
        await _dataSource.ExecuteAsync(_upsertSql,
            p => BindSaveParameters(p, session, snapshot, json), ct).ConfigureAwait(false);
    }

    public override async ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sessionId);
        var json = await _dataSource.QuerySingleOrDefaultAsync(_getByIdSql,
            p => p.Add(new NpgsqlParameter("id", sessionId)),
            static r => r.GetStringOrNull("snapshot"), ct).ConfigureAwait(false);
        return Deserialize(json)?.ToSession();
    }

    public override async ValueTask<CallSession?> GetByLinkedIdAsync(string linkedId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(linkedId);
        var json = await _dataSource.QuerySingleOrDefaultAsync(_getByLinkedIdSql,
            p => p.Add(new NpgsqlParameter("linked", linkedId)),
            static r => r.GetStringOrNull("snapshot"), ct).ConfigureAwait(false);
        return Deserialize(json)?.ToSession();
    }

    public override async ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var jsons = await _dataSource.QueryListAsync(_getActiveSql,
            static _ => { }, static r => r.GetStringOrNull("snapshot"), ct).ConfigureAwait(false);
        var sessions = new List<CallSession>();
        foreach (var json in jsons)
        {
            var snapshot = Deserialize(json);
            if (snapshot is not null) sessions.Add(snapshot.ToSession());
        }
        return sessions;
    }

    public override async ValueTask DeleteAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sessionId);
        await _dataSource.ExecuteAsync(_deleteSql,
            p => p.Add(new NpgsqlParameter("id", sessionId)), ct).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Rewrite `SaveBatchAsync` using the connection-level overload**

```csharp
    public override async ValueTask SaveBatchAsync(IReadOnlyList<CallSession> sessions, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var session in sessions)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = CallSessionSnapshot.FromSession(session);
                var json = Serialize(snapshot);
                await conn.ExecuteAsync(_upsertSql,
                    p => BindSaveParameters(p, session, snapshot, json), tx, ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
```

Also delete the now-unused `using System.Data;` if `DbType` is no longer referenced (it is not after this change).

- [ ] **Step 5: Build**

Run: `dotnet build src/Verbara.Sdk.Sessions.Postgres -c Release`
Expected: Build succeeded, 0 warnings (no IL2026/IL3050 — the `NoWarn` is gone and there is no Dapper to trip them).

### Task 1b.3: Run the Sessions.Postgres integration tests

**Files:** none

- [ ] **Step 1: Run the full Sessions.Postgres test suite**

Run: `dotnet test tests/Verbara.Sdk.Sessions.Postgres.Tests -c Release`
Expected: ALL PASS — including the 14/16 that the D.1 stubs canary made fail with `NotSupportedException`. Green here is the parity proof that the facade behaves identically to real Dapper.

- [ ] **Step 2: Commit**

```sh
git add src/Verbara.Sdk.Sessions.Postgres/ Verbara.Sdk/Directory.Packages.props
git commit -m "refactor(sessions-postgres): replace Dapper with Verbara.Sdk.Data.Npgsql facade

Reverts the D.1 stubs/Dapper.AOT canary. PostgresSessionStore now uses the
owned NpgsqlExecutor facade + explicit NpgsqlParameter binding. JsonbParameter
ICustomQueryParameter and DynamicParameters removed; JSONB bound via
NpgsqlDbType.Jsonb directly. All Sessions.Postgres integration tests green."
```

### Task 1b.4: Drop the stub package versions from SDK CPM, re-pack

**Files:**
- Modify: `Verbara.Sdk/Directory.Packages.props`

- [ ] **Step 1: Confirm nothing else references the stubs**

Run:
```sh
cd /media/Data/Source/Verbara/Verbara.Sdk
grep -rn "Verbara.Sdk.Dapper.Stubs\|Dapper.AOT\|InterceptorsPreviewNamespaces" src/ --include=*.csproj
```
Expected: no matches (Sessions.Postgres was the only consumer).

- [ ] **Step 2: Remove the stub + Dapper.AOT PackageVersion lines**

Delete lines 39–40 (`Verbara.Sdk.Dapper.Stubs` + `Dapper.AOT`) from `Verbara.Sdk/Directory.Packages.props`.

- [ ] **Step 3: Build the whole SDK + run full test suite**

Run:
```sh
dotnet build Verbara.Sdk.slnx -c Release
dotnet test -c Release 2>&1 | tail -5
```
Expected: 0 warnings; all ~3,079 tests green (matches Phase 0 baseline).

- [ ] **Step 4: Pack SDK (Sessions.Postgres + Data.Npgsql) to both feeds**

Run:
```sh
dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/
cp /media/Data/Source/Verbara/local-nuget-feed/Verbara.Sdk.Sessions.Postgres.*.nupkg \
   /media/Data/Source/Verbara/local-nuget-feed/Verbara.Sdk.Data.Npgsql.*.nupkg \
   /media/Data/Source/Verbara/Verbara.Platform/local-nuget-feed/
```

- [ ] **Step 5: Commit**

```sh
git add Verbara.Sdk/Directory.Packages.props
git commit -m "chore(sdk): drop Dapper.Stubs + Dapper.AOT from CPM (no consumers remain)"
```

---

## Phase 2 — Platform pilot: `Verbara.Platform.Storage.Postgres` + AOT-delta gate

> The proof-of-concept gate. This is the first package in `Verbara.Platform.Api`'s closure to leave Dapper; after it, the AOT diagnostic count MUST drop from 50.

### Task 2.1: Swap package references in the Platform storage csproj

**Files:**
- Modify: `src/Verbara.Platform.Storage.Postgres/Verbara.Platform.Storage.Postgres.csproj`
- Modify: `Verbara.Platform/Directory.Packages.props`

- [ ] **Step 1: Edit the storage csproj**

Remove `<PackageReference Include="Dapper" />` (and any `Dapper.AOT`/`Verbara.Sdk.Dapper.Stubs` references if present). Add `<PackageReference Include="Verbara.Sdk.Data.Npgsql" />`.

- [ ] **Step 2: Add the package version to Platform CPM**

In `Verbara.Platform/Directory.Packages.props`, add next to the Npgsql line (23):
```xml
    <PackageVersion Include="Verbara.Sdk.Data.Npgsql" Version="2.1.3" />
```
Leave the `Dapper` line for now (the Identity DataProtection repo still uses it until Task 2.6). Refresh the package cache + restore:
```sh
rm -rf ~/.nuget/packages/verbara.sdk.data.npgsql/
cd /media/Data/Source/Verbara/Verbara.Platform && dotnet restore
```

### Task 2.2: Migrate the template store `PostgresQueueStore`

**Files:**
- Modify: `src/Verbara.Platform.Storage.Postgres/Stores/PostgresQueueStore.cs`

> This store is the canonical template every other simple store follows. Migrate it fully, run its tests, then replicate the pattern.

- [ ] **Step 1: Replace usings + add the row Map() method**

Change `using Dapper;` → `using Verbara.Sdk.Data.Npgsql;`. Add to the `QueueRow` class:
```csharp
        public static QueueRow Map(NpgsqlDataReader r) => new()
        {
            queue_id = r.GetString("queue_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            is_active = r.GetBoolean("is_active"),
            max_waiting = r.GetInt32OrNull("max_waiting"),
            sla_targets = r.GetStringOrNull("sla_targets"),
            overflow_rule = r.GetStringOrNull("overflow_rule"),
            hours = r.GetStringOrNull("hours"),
            wrap_up = r.GetStringOrNull("wrap_up"),
            required_skills = r.GetString("required_skills"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
            created_by = r.GetStringOrNull("created_by"),
            updated_by = r.GetStringOrNull("updated_by"),
        };
```

- [ ] **Step 2: Rewrite `GetByIdAsync` + `ListAsync` to the facade**

```csharp
    public async Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT queue_id, tenant_id, name, is_active, max_waiting, sla_targets, overflow_rule, hours, wrap_up, " +
            "required_skills, created_at, updated_at, created_by, updated_by " +
            "FROM queue_configs WHERE tenant_id = @TenantId AND queue_id = @QueueId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("QueueId", queueId.Value)); },
            QueueRow.Map, ct);
        return row?.ToQueue();
    }

    public async Task<PagedResult<Queue>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var total = (int)(await _dataSource.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM queue_configs WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)), ct) ?? 0L);
        var rows = await _dataSource.QueryListAsync(
            "SELECT queue_id, tenant_id, name, is_active, max_waiting, sla_targets, overflow_rule, hours, wrap_up, " +
            "required_skills, created_at, updated_at, created_by, updated_by " +
            "FROM queue_configs WHERE tenant_id = @TenantId ORDER BY name LIMIT @Limit OFFSET @Offset",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Limit", query.PageSize)); p.Add(new NpgsqlParameter("Offset", query.Offset)); },
            QueueRow.Map, ct);
        var items = rows.Select(r => r.ToQueue()).ToList();
        return new PagedResult<Queue>(items, total, query.Page, query.PageSize);
    }
```
(Note `COUNT(*)` returns `bigint` → read as `long`, cast to `int` for `PagedResult`.)

- [ ] **Step 3: Rewrite `SaveAsync` + `DeleteAsync` to the facade**

`SaveAsync` keeps its JSON serialization and the `try/catch (PostgresException 23505)` exactly as-is; only the Dapper `conn.ExecuteAsync(sql, anon)` call changes to:
```csharp
        await _dataSource.ExecuteAsync(
            "INSERT INTO queue_configs (...) VALUES (...) ON CONFLICT (...) DO UPDATE SET ...",  // unchanged SQL
            p =>
            {
                p.Add(new NpgsqlParameter("QueueId", queue.QueueId.Value));
                p.Add(new NpgsqlParameter("TenantId", queue.TenantId.Value));
                p.Add(new NpgsqlParameter("Name", queue.Name));
                p.Add(new NpgsqlParameter("IsActive", queue.IsActive));
                p.Add(new NpgsqlParameter("MaxWaiting", (object?)queue.MaxWaiting ?? DBNull.Value));
                p.Add(new NpgsqlParameter("SlaTargets", (object?)slaJson ?? DBNull.Value));
                p.Add(new NpgsqlParameter("OverflowRule", (object?)overflowJson ?? DBNull.Value));
                p.Add(new NpgsqlParameter("Hours", (object?)hoursJson ?? DBNull.Value));
                p.Add(new NpgsqlParameter("WrapUp", wrapUpJson));
                p.Add(new NpgsqlParameter("RequiredSkills", skillsJson));
                p.Add(new NpgsqlParameter("CreatedAt", queue.CreatedAt));
                p.Add(new NpgsqlParameter("UpdatedAt", (object?)queue.UpdatedAt ?? DBNull.Value));
                p.Add(new NpgsqlParameter("CreatedBy", (object?)queue.CreatedBy ?? DBNull.Value));
                p.Add(new NpgsqlParameter("UpdatedBy", (object?)queue.UpdatedBy ?? DBNull.Value));
            }, ct);
```
The `::jsonb` casts in the SQL stay; passing the JSON as a `string` parameter with the `::jsonb` cast is unchanged behavior. `DeleteAsync` becomes a one-line `ExecuteAsync` with the two id params.

- [ ] **Step 4: Build + run the queue store tests**

Run:
```sh
dotnet build src/Verbara.Platform.Storage.Postgres -c Release
dotnet test tests/Verbara.Platform.Api.Tests -c Release --filter "FullyQualifiedName~Queue" 2>&1 | tail -10
```
Expected: 0 warnings; queue-related Postgres store tests green. (If queue storage has dedicated IT coverage in a `*.Storage.Postgres.Tests` project, run that instead.)

- [ ] **Step 5: Commit**

```sh
git add src/Verbara.Platform.Storage.Postgres/Stores/PostgresQueueStore.cs \
        src/Verbara.Platform.Storage.Postgres/Verbara.Platform.Storage.Postgres.csproj \
        Verbara.Platform/Directory.Packages.props
git commit -m "refactor(storage-postgres): migrate PostgresQueueStore to Npgsql facade (template)"
```

### Task 2.3: Migrate the remaining simple stores (per-store checklist)

**Files:** the remaining ~28 stores in `src/Verbara.Platform.Storage.Postgres/Stores/` that follow the simple shape.

> Each store follows the Task 2.2 template exactly. This is mechanical and subagent-friendly (one subagent per store or per small batch). For EACH store, the loop is:

- [ ] **For each store file:**
  1. `using Dapper;` → `using Verbara.Sdk.Data.Npgsql;`
  2. Add `public static T Map(NpgsqlDataReader r)` to each `*Row` class using the name-based getters (Task 2.2 Step 1 pattern). NULL columns → `*OrNull`; JSONB text columns → `GetString`/`GetStringOrNull` (deserialization stays in `ToX()`).
  3. Each `conn.Query*<TRow>(sql, anon)` → `_dataSource.QuerySingleOrDefaultAsync`/`QueryListAsync(sql, bind, TRow.Map, ct)`.
  4. Each `conn.Execute*(sql, anon)` → `_dataSource.ExecuteAsync`/`ExecuteScalarAsync(sql, bind, ct)`.
  5. `COUNT(*)` scalars → `ExecuteScalarAsync<long>`, cast to `int` at the call site.
  6. Remove the `await using var conn = ...OpenConnectionAsync` lines made redundant by the facade (keep them only for explicit transaction blocks → use the connection-level overload).
  7. Build the package (0 warnings) and run that store's tests green before moving on.
  8. Commit per store: `refactor(storage-postgres): migrate Postgres<X>Store to Npgsql facade`.

**Checklist of simple stores** (verify against `ls src/Verbara.Platform.Storage.Postgres/Stores/` at execution time; tick each):
- [ ] PostgresContactStore  - [ ] PostgresConversationStore (also has DynamicParameters — see 2.5)  - [ ] PostgresMessageStore
- [ ] PostgresTeamStore  - [ ] PostgresUserStore  - [ ] PostgresRoleStore  - [ ] PostgresApiKeyStore
- [ ] PostgresTagStore  - [ ] PostgresCaseStore  - [ ] PostgresDispositionStore  - [ ] PostgresCannedResponseStore
- [ ] PostgresSurveyStore  - [ ] PostgresChannelConfigStore  - [ ] PostgresWebhookSubscriptionStore
- [ ] PostgresTenantStore  - [ ] PostgresTenantSettingsStore  - [ ] PostgresBillingStore  - [ ] (… remaining per `ls`)

### Task 2.4: Migrate the dynamic-WHERE store `PostgresPurgeLogStore`

**Files:**
- Modify: `src/Verbara.Platform.Storage.Postgres/Stores/PostgresPurgeLogStore.cs`

- [ ] **Step 1: Add `PurgeLogRow.Map`** (per Task 2.2 Step 1 pattern; columns: purge_id, tenant_id, subject_type, subject_id, performed_by, reason, entities_deleted, purged_at → all `GetString` except `purged_at` = `GetDateTime`).

- [ ] **Step 2: Rewrite `SaveAsync`** to `_dataSource.ExecuteAsync` with explicit params (entities JSON as `string` param with `::jsonb` cast — unchanged).

- [ ] **Step 3: Rewrite `ListAsync` — replace `DynamicParameters` with a conditions list + bind closure**

```csharp
    public async Task<PagedResult<PurgeEntry>> ListAsync(
        string? tenantId, DateTimeOffset? from, DateTimeOffset? until,
        int page, int pageSize, CancellationToken ct)
    {
        var conditions = new List<string>();
        var binders = new List<Action<NpgsqlParameterCollection>>();
        if (!string.IsNullOrEmpty(tenantId))
        {
            conditions.Add("tenant_id = @TenantId");
            binders.Add(p => p.Add(new NpgsqlParameter("TenantId", tenantId)));
        }
        if (from.HasValue)
        {
            conditions.Add("purged_at >= @From");
            binders.Add(p => p.Add(new NpgsqlParameter("From", from.Value)));
        }
        if (until.HasValue)
        {
            conditions.Add("purged_at <= @Until");
            binders.Add(p => p.Add(new NpgsqlParameter("Until", until.Value)));
        }
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var offset = (page - 1) * pageSize;

        void BindFilters(NpgsqlParameterCollection p) { foreach (var b in binders) b(p); }

        var total = (int)(await _dataSource.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM purge_log {where}", BindFilters, ct) ?? 0L);

        var rows = await _dataSource.QueryListAsync(
            "SELECT purge_id, tenant_id, subject_type, subject_id, performed_by, reason, entities_deleted, purged_at " +
            $"FROM purge_log {where} ORDER BY purged_at DESC LIMIT @Limit OFFSET @Offset",
            p => { BindFilters(p); p.Add(new NpgsqlParameter("Limit", pageSize)); p.Add(new NpgsqlParameter("Offset", offset)); },
            PurgeLogRow.Map, ct);

        var items = rows.Select(r => r.ToPurgeEntry()).ToList();
        return new PagedResult<PurgeEntry>(items, total, page, pageSize);
    }
```

- [ ] **Step 4: Build + test + commit**

Run: `dotnet build src/Verbara.Platform.Storage.Postgres -c Release && dotnet test tests/Verbara.Platform.Api.Tests -c Release --filter "FullyQualifiedName~Purge"`
Expected: 0 warnings, green. Commit: `refactor(storage-postgres): migrate PostgresPurgeLogStore (dynamic WHERE) to facade`.

### Task 2.5: Migrate the remaining special-handling Platform stores

**Files:** `PostgresAuditStore.cs`, `RoleTemplateSeeder.cs`, `PostgresAuthEventStore.cs`, `PostgresConversationStore.cs`, `PostgresAgentStore.cs` (the Platform DynamicParameters/CommandDefinition sites per the spec's special-handling list).

- [ ] **For each:** apply the Task 2.4 dynamic-WHERE pattern (for `DynamicParameters`) or the Task 2.2 template (for `CommandDefinition` → facade method with CT). Audit IT coverage first; if thin, add a round-trip test BEFORE migrating (TDD). Build 0 warnings + tests green + commit per file.

### Task 2.6: Migrate `DapperXmlRepository` (Identity DataProtection)

**Files:**
- Modify: `src/Verbara.Platform.Identity/DataProtection/DapperXmlRepository.cs`
- Modify: `src/Verbara.Platform.Identity/Verbara.Platform.Identity.csproj` (drop Dapper, add Data.Npgsql if not transitive)
- Modify: `Verbara.Platform/Directory.Packages.props` (remove `Dapper` line — last consumer)

- [ ] **Step 1:** Read the file; it is simple-shape (Phase B output). Migrate per Task 2.2 template. Rename the class `DapperXmlRepository` → `NpgsqlXmlRepository` and update its DI registration reference in `Program.cs` / the Identity DI extension (grep for `DapperXmlRepository`).

- [ ] **Step 2:** Confirm no `Dapper` references remain anywhere in Platform:
```sh
grep -rn "using Dapper\|Include=\"Dapper\"" src/ --include=*.cs --include=*.csproj
```
Expected: no matches. Remove the `Dapper` `PackageVersion` line from `Directory.Packages.props`.

- [ ] **Step 3:** Build + commit: `refactor(identity): NpgsqlXmlRepository replaces DapperXmlRepository; drop Dapper from Platform`.

### Task 2.7: Full Platform test suite green

**Files:** none

- [ ] **Step 1: Run the whole solution**

Run: `cd /media/Data/Source/Verbara/Verbara.Platform && dotnet test Verbara.Platform.slnx -c Release 2>&1 | tail -8`
Expected: Platform.Api 943 + Realtime 22 + any storage IT — zero new failures vs Phase 0 baseline. 0 build warnings.

### Task 2.8: AOT-delta gate (the proof-of-concept)

**Files:** none (measurement)

- [ ] **Step 1: Re-run the AOT publish**

Run:
```sh
cd /media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api
dotnet publish Verbara.Platform.Api.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishAot=true -p:InvariantGlobalization=true -p:TrimmerSingleWarn=false \
  -o /tmp/aot-phase2/ 2>&1 | tee /tmp/aot-phase2.log || true
grep -cE "IL2026|IL3050|IL2046|IL2060|IL2067|IL2070|IL2075|IL2080" /tmp/aot-phase2.log
```
Expected: **count drops substantially below the Phase 0 baseline of 50.** The Storage.Postgres + Sessions.Postgres surfaces are now Dapper-free; the residual diagnostics (if any) are 100% attributable to the un-migrated Pro storage packages still pulling real Dapper.

- [ ] **Step 2: Record the result**

Write the before/after counts and the per-assembly attribution of any residual diagnostics to `docs/operations/phase-d-validation/2026-05-19-pilot-aot-delta.md`. **This is the gate:** a non-zero drop confirms the raw-Npgsql approach works at Platform scale and Phases 3–6 (the Pro sweep) will close the rest. A zero drop means a non-Dapper residual blocker exists → STOP and investigate before sweeping.

- [ ] **Step 3: Commit the validation report**

```sh
git add docs/operations/phase-d-validation/2026-05-19-pilot-aot-delta.md
git commit -m "docs(adr-0022): Phase D pilot AOT-delta gate result"
```

---

## Phases 3–6 — Roadmap (own implementation plan, written after Task 2.8 validates)

These are intentionally NOT decomposed into bite-sized tasks yet — the pilot teaches the exact playbook. They will get `docs/plans/active/2026-05-2x-phase-d-sweep-and-cutover.md`.

- **Phase 3 — Pro sweep (7 packages).** One `dapper-aot-migration` subagent per package using the Task 2.2/2.4 templates. Wave 1 (Dialer, EventStore, Cluster, Realtime); Wave 2 special-handling (Analytics → PostgresLiveQueueSnapshotStore CommandDefinition; CallAnalytics → DynamicParameters; AgentAssist → SnoopChannelManager). Replace `TypeHandler` (`DateOnly`, `Metadata`) with `GetDateOnly` + JSON source-gen. Drop `Dapper` from Pro CPM. Re-pack Pro to both feeds.
- **Phase 4 — Platform.Api AOT flip + triple gate.** Edit `Verbara.Platform.Api.csproj` (`IsAotCompatible=true`, `PublishAot=true`, `InvariantGlobalization=true`, remove analyzer disables). G1 AOT publish clean (0 diagnostics, native ELF, no managed Verbara DLLs); G2 full cross-repo test matrix; G3 AOT image E2E smoke (`docker/Dockerfile.api-aot`).
- **Phase 5 — Image cutover.** Final pack/tag/push 3 repos, ghcr.io AOT images, regen `authorized-digests.json`, SMB manuales image-tag updates, OCI-deprecate old IL tags.
- **Phase 6 — 24h AOT soak (mandatory gate).** Re-run D-LK profile vs the IL baseline (p99 avg 60.66 ms, 0 fails, 12–13 conns). Equal-or-better required for production-readiness sign-off.

**On Phase D completion:** `git mv docs/plans/active/2026-05-19-phase-d-dapper-aot.md docs/plans/archived/` (Option O, superseded) + `git mv` this plan + the sweep plan to `completed/`; append ADR-0022 Amendment §8.
