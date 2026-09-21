# Tasks: docs-hygiene-sweep (Platform host — path-leak purge in living specs)

All edits below are token-level path substitutions applied at `/xr:apply` time. Convention:
cd/pack instructions → workspace-relative (`../Verbara.Sdk.Pro`, `../local-nuget-feed/`);
cross-repo file citations → repo-qualified relative. Spanish prose keeps its language — only the
path token changes. Each occurrence was re-verified with `grep -n '/media/Data/Source/'` before
this inventory was written.

## 1. Doctor-named spec docs (5 files)

- [x] 1.1 `docs/specs/2026-03-31-v121-operations-design.md` — line 621
      (`dotnet pack ... -o /media/Data/Source/Verbara/local-nuget-feed/` → `../local-nuget-feed/`).
- [x] 1.2 `docs/specs/2026-04-07-sprint0-security-fixes-design.md` — lines 193, 195, 198
      (193 `cd .../Verbara.Sdk.Pro` → `../Verbara.Sdk.Pro`; 195 pack `-o .../local-nuget-feed/`
      → `../local-nuget-feed/`; 198 `cd .../Verbara.Platform` → workspace-relative).
- [x] 1.3 `docs/specs/2026-04-07-sprint1-suspension-settings-design.md` — line 286
      (`cd .../Verbara.Platform` → workspace-relative).
- [x] 1.4 `docs/specs/2026-04-07-sprint2-features-dunning-design.md` — lines 409, 552, 554, 557
      (409 `Modify: .../Verbara.Sdk.Pro/src/.../TenantStatus.cs` cross-repo cite →
      `../Verbara.Sdk.Pro/src/.../TenantStatus.cs`; 552 `cd .../Verbara.Sdk.Pro`; 554 pack
      `-o .../local-nuget-feed/`; 557 `cd .../Verbara.Platform`).
- [x] 1.5 `docs/specs/2026-04-19-product-strategy-v2.md` — lines 328, 770
      (both `/media/Data/Source/Verbara/local-nuget-feed/` → `../local-nuget-feed/`; line 770 is
      inside Spanish prose — rewrite the path token only, keep the language).

## 2. Phase-D spec docs the doctor's truncated evidence missed (3 files)

Re-verified 2026-07-12 with `grep -n '/media/Data/Source/'`:

- [x] 2.1 `docs/specs/2026-05-19-phase-d-dapper-aot-migration-design.md` — line 200 (×1)
      (`Verbara/local-nuget-feed/` → `../local-nuget-feed/`; the sibling
      `Verbara.Platform/local-nuget-feed/` reference on the same line is already repo-relative).
- [x] 2.2 `docs/specs/2026-05-19-phase-d-dapper-removal-raw-npgsql-design.md` — lines 137, 172,
      181, 182, 183 (×5) (137 `.../local-nuget-feed/`; 172 `cd .../Verbara.Platform/src/...`;
      181 `cd .../Verbara.Platform`; 182 `cd .../Verbara.Sdk.Pro`; 183 `cd .../Verbara.Sdk`).
- [x] 2.3 `docs/specs/2026-05-19-verbara-sdk-dapper-stubs-design.md` — lines 324, 326, 328, 329
      (×4) (324 `cd .../Verbara.Sdk`; 326 pack `-o .../local-nuget-feed/`; 328 `cp
      .../local-nuget-feed/...nupkg`; 329 `.../Verbara.Platform/local-nuget-feed/` destination).

## 3. Ledger + validation

- [x] 3.1 Add a `CHANGELOG.md` `[Unreleased]` entry recording the path-leak purge in the 8
      `docs/specs/` documents (required by the `/xr:apply` commit gate — ledger row 10).
- [x] 3.2 `grep -rn '/media/Data/Source/' docs/specs/` returns no matches across the 8 in-scope
      files (the `tests/**` load-test transcripts stay leaked by design — out of scope).
- [x] 3.3 `npx -y @fission-ai/openspec@1.6.0 validate --all --strict --no-interactive` — MUST pass.
- [x] 3.4 `/xr:doctor` re-scan confirms the `path-leak:Verbara.Platform` WARN family clears for the authored `docs/specs/` prose — CONFIRMED 2026-09-20: `bash ../verbara-meta/scripts/xr-doctor.sh` names exactly 2 offending tracked files, `openspec/changes/fix-ip-host-tenant-resolution/design.md` + `tasks.md`, **neither under `docs/specs/`**, and a maintainer-path grep over `docs/specs/` returns 0 matches across all 39 files; the sibling train legs cleared too (`PASS path-leak:Verbara.Sdk`, `PASS path-leak:Verbara.Sdk.Pro`), which is the post-train precondition this box was left open for. The check still WARNs, but on prose authored 2026-08-22 (PR #267) — 40 days after this change archived, outside its scope — and both hits cite the workspace-root `CLAUDE.md`, which sits outside any git repo and has no repo-relative form. Keeping the check green needs a standing in-repo guard (Platform has none today) rather than another sweep; that is a separate concern from this box.
