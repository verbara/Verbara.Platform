## ADDED Requirements

### Requirement: Living-spec documents SHALL use portable cross-repo paths

Tracked documents under `docs/specs/` SHALL express cross-repo and local-feed locations as
portable paths — workspace-relative for cd/pack instructions (e.g. `../Verbara.Sdk.Pro`,
`../local-nuget-feed/`) and repo-qualified relative for cross-repo file citations — and SHALL NOT
hard-code absolute machine paths (`/media/Data/Source/...`). This aligns the living specs with
`openspec/config.yaml`'s proposal-artifact public-repo content rule (verbara-meta/ADR-0005), which
already bans absolute machine paths, and with verbara-meta/ADR-0007.

#### Scenario: cd/pack instruction cites the local NuGet feed

- **WHEN** a `docs/specs/` document gives a `dotnet pack ... -o <feed>` or `cd <repo>` instruction
- **THEN** the path token is workspace-relative (e.g. `../local-nuget-feed/`, `../Verbara.Sdk.Pro`)
  and contains no `/media/Data/Source/` prefix

#### Scenario: cross-repo file citation

- **WHEN** a `docs/specs/` document cites a source file that lives in a sibling repo
- **THEN** the citation uses a repo-qualified relative path (e.g.
  `../Verbara.Sdk.Pro/src/.../TenantStatus.cs`) rather than an absolute machine path

#### Scenario: Spanish prose is preserved

- **WHEN** a path token appears inside Spanish-language prose in a dated design document
- **THEN** only the path token is rewritten; the surrounding language, wording, and date are left
  unchanged (these are period-correct historical design records)

### Requirement: The path-leak WARN family SHALL clear after the sweep

After this change is applied, a `grep` for `/media/Data/Source/` across the 8 in-scope
`docs/specs/` documents SHALL return no matches, so the `/xr:doctor` `path-leak:Verbara.Platform`
WARN family clears for authored prose. Machine transcripts explicitly out of scope (the
`tests/**` load-test reports) are handled by a separate verbara-meta exemption sidecar and are not
covered by this requirement.

#### Scenario: doctor re-run after apply

- **WHEN** `/xr:doctor` re-scans Platform's `docs/specs/` after this change is applied
- **THEN** no `path-leak:Verbara.Platform` WARN is raised for the 8 in-scope documents

## Architectural Risk

**Level:** LOW

**Affected:** `docs/specs/` authored prose only — no `src/**`, no `tests/**`, no build, no runtime,
no API. Cross-repo blast radius is limited to the Sdk/Pro child legs of the same train, which carry
their own inventories.

**Mitigation:** Token-level substitution against a verified file:line inventory (enumerated in
`tasks.md`), each occurrence re-grepped before edit; dated content and Spanish wording untouched;
`openspec validate --all --strict` gates the change and the existing `/xr:doctor` re-scan confirms
the WARN family clears without introducing new leaks.
