---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Verbara ecosystem maintainers (living-spec readers), cross-repo /xr:doctor hygiene
decision_ref: verbara-meta/ADR-0007
---

# Proposal: docs-hygiene-sweep (Platform host — path-leak purge in living specs)

## Why

Eight tracked Platform spec docs under `docs/specs/` hard-code absolute machine paths
(`/media/Data/Source/...`) in their cd/pack instructions and cross-repo file citations. Those
absolute paths are non-portable (they only resolve on one operator's checkout), and `/xr:doctor`
has flagged the family as a standing WARN (`path-leak:Verbara.Platform`, yellow since doctor run 1);
`openspec/config.yaml`'s proposal rule already bans absolute machine paths in change artifacts
(public-repo content rule, verbara-meta/ADR-0005), so leaving the same tokens in the living specs
is an unresolved inconsistency. This is the Platform (host) leg of the cross-repo
`docs-hygiene-sweep` train; the Sdk and Pro legs arrive as child changes via `/xr:propagate`.

## What Changes

- Replace every `/media/Data/Source/Verbara/...` absolute-path token in the 8 in-scope
  `docs/specs/` documents with a portable form, per the fixed conventions in the contract
  (`impact.yaml`) and verbara-meta/ADR-0007:
  - **cd/pack instructions** → workspace-relative (e.g. `../Verbara.Sdk.Pro`, `../local-nuget-feed/`).
  - **cross-repo file citations** → repo-qualified relative paths.
  - Spanish prose keeps its language; only the path token is rewritten (no content/date rewrites —
    these are dated, period-correct design docs).
- Add a `CHANGELOG.md` `[Unreleased]` entry recording the hygiene sweep (the `/xr:apply` commit
  gate requires it).
- No product code, no runtime behavior, no test, and no API changes — this is authored-prose
  hygiene only.

## Capabilities

### New Capabilities

- `docs-path-hygiene`: living-spec documents SHALL express cross-repo locations as portable
  (workspace-relative / repo-qualified relative) paths, never absolute machine paths, keeping the
  living specs consistent with `openspec/config.yaml`'s public-repo content rule and clearing the
  `path-leak:Verbara.Platform` `/xr:doctor` WARN family.

### Modified Capabilities

(none — no existing living spec in `openspec/specs/` has its requirements changed; the 8 affected
files are `docs/specs/` design documents, not OpenSpec living specs.)

## Impact

- **Docs:** 8 files under `docs/specs/` (the concrete file:line inventory is enumerated in
  `tasks.md`) — token-level path substitutions only.
- **Code / APIs / Dependencies:** none. No `src/**`, no `tests/**`, no `.csproj`, no workflow
  changes. Native AOT surface and all runtime behavior unchanged.
- **Cross-repo:** host leg of the `docs-hygiene-sweep` train (`impact.yaml`,
  decision_ref verbara-meta/ADR-0007). Sdk (`sdk/docs-hygiene-sweep`) and Pro
  (`pro/docs-hygiene-sweep`) child changes fan out via `/xr:propagate`; they carry their own
  inventories and are out of scope here.
- **Out of scope (this repo):** `tests/**` load-test report transcripts (dated NBomber/soak
  output) — those retain their absolute paths and gain a `path-leak` exemption via a separate
  verbara-meta sidecar PR (verbara-meta hosts mechanisms, never changes — ADR-0005), not this
  change.
