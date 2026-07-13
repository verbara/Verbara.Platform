## Context

This design is intentionally minimal: `docs-hygiene-sweep` is a docs-only, token-level path
substitution with no genuine design decision to make — the replacement conventions are fixed by
the cross-repo contract (`impact.yaml`) and verbara-meta/ADR-0007, and mirror the established
Pro precedent (commit c0f56b5 / #19: feed path → relative, sibling repos → repo-relative). It
exists only because `tasks` declares `design` as a dependency in this schema; the substance lives
in `proposal.md`, the `docs-path-hygiene` spec, and the verified inventory in `tasks.md`.

## Goals / Non-Goals

**Goals:**
- Remove every `/media/Data/Source/Verbara/...` absolute-path token from the 8 in-scope
  `docs/specs/` documents, replacing each with the portable form the contract prescribes.
- Clear the `/xr:doctor` `path-leak:Verbara.Platform` WARN family for authored prose.

**Non-Goals:**
- No product code, test, workflow, `.csproj`, or runtime change.
- No rewrite of dated/period-correct content or Spanish prose beyond the path token itself.
- The `tests/**` load-test transcripts (machine output) — exempted via a separate verbara-meta
  sidecar, not this change.
- The Sdk/Pro child legs — fanned out by `/xr:propagate`, out of scope for the host.

## Decisions

- **Conventions are inherited, not chosen here.** cd/pack instructions → workspace-relative
  (`../Verbara.Sdk.Pro`, `../local-nuget-feed/`); cross-repo file citations → repo-qualified
  relative. This matches `openspec/config.yaml`'s public-repo content rule (verbara-meta/ADR-0005)
  and verbara-meta/ADR-0007. Alternative considered and rejected: keeping absolute paths "for
  operator convenience" — rejected because it is exactly the WARN being retired and it contradicts
  the config rule already enforced on change artifacts.
- **Substitution is inventory-driven, not global sed.** Each occurrence is re-verified with
  `grep -n '/media/Data/Source/'` immediately before editing (done at propose time for the phase-d
  trio) so no in-prose reference is missed and no false hit (e.g. inside a code fence that is a
  genuine runtime value) is rewritten blind. Edits happen at `/xr:apply` time, not now.

## Risks / Trade-offs

- [A path token is embedded in a way that changes meaning if rewritten] → Mitigation: the inventory
  is file:line-precise and each edit is scoped to the leaked prefix only; the `openspec validate`
  gate plus the `/xr:doctor` re-scan confirm no residual leak and no new one.
- [Divergence from the Sdk/Pro child legs] → Mitigation: all three legs share the one contract
  (`impact.yaml`) and the same convention; the boundary reconciliation matrix from `/xr:propagate`
  keeps them aligned.

## Migration Plan

No deployment or rollback surface — the change touches only tracked documentation. Rollback is a
plain `git revert` of the docs commit. Applied per the `/xr:apply` staging for the host repo.
