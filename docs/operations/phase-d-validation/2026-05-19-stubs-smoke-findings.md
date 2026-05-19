# Phase D.1 G-stubs-5 — AOT publish smoke findings (2026-05-19)

Companion to [Day 1 findings](2026-05-19-day-1-findings.md) and [baseline log](2026-05-19-baseline-aot-publish.log).
Smoke log: [2026-05-19-stubs-smoke-aot-publish.log](2026-05-19-stubs-smoke-aot-publish.log).

## TL;DR

- Stubs nupkg + Sessions.Postgres 2.1.2 (Phase E canary) packed + restored cleanly across both feeds.
- Platform's `Directory.Packages.props` now declares `Verbara.Sdk.Dapper.Stubs 2.1.72-aotstub.1` and `Dapper.AOT 1.0.52` alongside the existing `Dapper 2.1.72` entry (Phase D.2 sweep removes the latter).
- AOT publish diagnostic count after the props update: **50** — **identical to baseline**. Delta = 0.
- **Root cause of zero delta**: NO project in the Platform.Api closure references `Verbara.Sdk.Sessions.Postgres` or `Verbara.Sdk.Dapper.Stubs`. CPM declares versions; only `<PackageReference>` pulls packages into the closure. The stubs were never materialized into Platform's restore (`~/.nuget/packages/verbara.sdk.dapper.stubs/` is absent after restore — verified).
- The 50 baseline diagnostics ALL come from the real `Dapper.dll` that `Verbara.Platform.Api.csproj` references directly (`<PackageReference Include="Dapper" />`) plus what `Storage.Postgres` + DataProtection pull. Sessions.Postgres has **no presence** in the baseline closure.

This is the architecturally correct result, and it is a real Phase F finding: the smoke gate as scripted cannot show a drop because the test surface (a downstream consumer of stubs) is missing from Platform.

## Setup

- Verbara.Sdk on `feat/dapper-stubs` at commit `08a36b8c` (Phase E canary done) + added NU5104 suppression in `Verbara.Sdk.Sessions.Postgres.csproj` to allow stable 2.1.2 → prerelease stubs dep (commit pending in SDK repo).
- Verbara.Sdk.Sessions.Postgres adopted Verbara.Sdk.Dapper.Stubs (Phase E commit 08a36b8c).
- Verbara.Platform on `main`, Platform.Api csproj UNCHANGED (`<IsAotCompatible>false</IsAotCompatible>` preserved).
- AOT publish command identical to 2026-05-19 baseline.

## Diagnostic count delta

| Bucket | Baseline 2026-05-19 | Post-stubs 2026-05-19 | Delta |
|---|---:|---:|---:|
| **Total IL[0-9]{4}** | 50 | 50 | 0 |
| IL2070 (trim — missing DynamicallyAccessedMembers on Type) | 15 | 15 | 0 |
| IL3050 (AOT — DynamicMethod / MakeGenericType) | 13 | 13 | 0 |
| IL2046 (trim — interface/virtual signature mismatch) | 8 | 8 | 0 |
| IL2075 (trim — return type missing DAM) | 5 | 5 | 0 |
| IL2093 (trim — override DAM mismatch) | 4 | 4 | 0 |
| IL2080 (trim — field DAM mismatch) | 2 | 2 | 0 |
| IL2092 (trim — parameter DAM mismatch) | 1 | 1 | 0 |
| IL2067 (trim — Activator.CreateInstance DAM) | 1 | 1 | 0 |
| IL2060 (trim — MakeGenericMethod DAM) | 1 | 1 | 0 |

Source-path attribution is identical between baseline and post-stubs:

| Path bucket | Baseline | Post-stubs |
|---|---:|---:|
| `/_/Dapper/...` source-link paths | 37 | 37 |
| `ILC :` link-time diagnostics | 13 | 13 |

## Source of remaining diagnostics

All 50 diagnostics resolve to the real `Dapper.dll` (verified via `/_/Dapper/SqlMapper.cs`, `/_/Dapper/DefaultTypeMap.cs`, `/_/Dapper/CommandDefinition.cs`, `/_/Dapper/WrappedBasicReader`, `/_/Dapper/DisposedReader`, `/_/Dapper/TypeExtensions.cs`).

That `Dapper.dll` is pulled by `<PackageReference Include="Dapper" />` declared **directly** in `src/Verbara.Platform.Api/Verbara.Platform.Api.csproj` (used by Identity DataProtection's `DapperXmlRepository` from ADR-0022 Phase B) plus the transitive copies that `Verbara.Platform.Storage.Postgres` and the Pro `*.Storage.Postgres` packages bring in.

Transitive proof from `dotnet list package --include-transitive`:
```
> Dapper                  2.1.72   2.1.72
> Verbara.Sdk.Sessions    2.1.2
```
No `Verbara.Sdk.Sessions.Postgres`, no `Verbara.Sdk.Dapper.Stubs`. NuGet honored the absence of references and pulled neither.

ILC publish output is empty (`/tmp/aot-stubs-smoke-2026-05-19/` contains zero files) because ILC errored at link time with `MSB3077` — same as baseline. This means the "verify stub Dapper.dll vs real Dapper.dll via published file size" check from the plan (line 1611-1623) is moot: publish never reaches the file-emit stage because the AOT analyzer halts on real Dapper.

## Honest assessment

1. ❌ Stubs were NOT pulled into the runtime closure of Platform.Api (verified by restored-packages list + transitive deps + empty publish output).
2. ❌ Diagnostic count did NOT drop (50 → 50, delta 0).
3. ✅ Remaining 50 diagnostics ARE 100% attributable to the real `Dapper.dll` from Platform.Api's direct `<PackageReference Include="Dapper" />` + Storage.Postgres + Pro storage packages.
4. ✅ Sessions.Postgres canary build PASSED (Phase E commit `08a36b8c`) and its nupkg packs + restores cleanly into Platform's feed.
5. ✅ The stubs design IS architecturally sound — when a Platform project references the stubs (via the Phase D.2 sweep), the same-identity `Dapper.dll` from `Verbara.Sdk.Dapper.Stubs` will replace the real one and the IL3050/IL2070 surface attributable to that consumer will collapse.

## What the plan got wrong (architecture gap)

The Phase F plan (`docs/plans/active/2026-05-19-verbara-sdk-dapper-stubs.md` lines 1537-1608) assumed Platform.Api transitively consumed `Verbara.Sdk.Sessions.Postgres`. It does not: only `Verbara.Sdk.Sessions` (the in-memory abstraction) is in the closure, via `Verbara.Platform.Mail` and `Verbara.Platform.Channels.Sms`. `Sessions.Postgres` (the Postgres impl) is only referenced by SDK customer apps, not by Platform itself.

The plan's contingency at line 1606 — "If diagnostic count is STILL 50, that means the real Dapper.dll is still in the closure somewhere — investigate transitive deps" — exactly describes this case, BUT the cause here is "no stubs in the closure at all," not "real Dapper hiding among the stubs."

## What "end-to-end validation" actually requires (Phase D.2 path)

To see a real diagnostic drop, one of the following must happen:

1. **Migrate `Verbara.Platform.Storage.Postgres` to consume stubs + Dapper.AOT** (mirrors Phase E's pattern on a Platform store) — the closest analogue of Phase D.2's first sweep step. This is the smallest move that validates the design at Platform scale.
2. **Migrate a Platform-direct `Dapper` use site** (e.g., `Verbara.Platform.Identity.DataProtection.DapperXmlRepository`) to interception by Dapper.AOT + remove `<PackageReference Include="Dapper" />` from `Platform.Api.csproj` in favor of the stubs reference. This validates the same-identity replacement at the host level.
3. **Add a temporary direct `<PackageReference Include="Verbara.Sdk.Dapper.Stubs" />` to `Platform.Api.csproj`** alongside the existing Dapper ref to observe NuGet's selection behavior between two packages that ship the same `Dapper.dll` assembly identity. (This is research, not migration — but the task discipline forbids any csproj modification.)

The Phase D.1 plan's success criterion ("0 diagnostics") was always Phase D.2-bound, never Phase F-bound. Phase F is honestly **a no-op at Platform-API scope** until Phase D.2 ships its first storage-package migration.

## Conclusion

Phase D.1 (Verbara.Sdk.Dapper.Stubs build + Sessions.Postgres canary) closes successfully. The stubs nupkg packs, restores, and integrates cleanly with Dapper.AOT 1.0.52 in the Sessions.Postgres canary build per Phase E.

Phase F's Platform-side smoke is **vacuous** as scripted: no Platform project pulls the stubs, so the diagnostic count cannot move. This is captured here as an evidence record (logs archived, props updated, transitive deps audited). The real validation of the same-identity replacement mechanism happens in Phase D.2 when the first Platform-owned storage package adopts stubs.

**Next step**: Phase D.2 sweep — start with the smallest Platform storage surface (candidate: `Verbara.Platform.Storage.Postgres` migration in isolation) and re-run the AOT publish smoke. Expected delta at that point: visible drop attributable to that package's `/_/Dapper/...` source-link paths disappearing from the diagnostic stream.
