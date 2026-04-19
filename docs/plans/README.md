# Execution Plans

Concrete, dated plans for delivering work. Plans are the **how** — sprint breakdowns, task lists, acceptance criteria. They are paired with specs (the **what**) and ADRs (the **why**).

## Lifecycle

```
active/      ← plan in progress
  ↓ (on ship)
completed/   ← plan delivered; preserved as historical record (append-only)

archived/    ← superseded, abandoned, or draft-only plans (append-only)
```

- **`active/`** — exactly the plans currently being executed. A fresh Claude session looking at `ls docs/plans/active/` should see what's in flight.
- **`completed/`** — plans that shipped. Never edit after moving here. Useful to compare future plans against historical scope + execution notes.
- **`archived/`** — skeletons that were never executed, plans replaced by a newer version (add a top-line pointer to the successor), or work that was parked.

## File convention

`{YYYY-MM-DD}-{plan-id}-{short-descriptor}.md` — date-prefixed for chronological sort.

`{plan-id}` = stable identifier (e.g. `plan32c`, `v160-realtime-presence`). The same identifier may appear in multiple folders as the plan moves through its lifecycle.

## How plans relate to specs + ADRs

- **Spec** (`docs/specs/`): design of the thing being built. Answers "what + how does it work?"
- **ADR** (`docs/decisions/`): single architectural decision. Answers "why this approach over others?"
- **Plan** (`docs/plans/`): execution track. Answers "in what order, with what tests, by when?"

A plan may reference one spec and several ADRs. A completed plan makes its corresponding spec history — the code + tests are the living version.

## ExitPlanMode integration

When `ExitPlanMode` is approved, Claude writes the detailed plan to `~/.claude/plans/{auto-slug}.md`. Copy that file into `docs/plans/active/` with a meaningful date-prefixed name **before starting execution** so the repo-local copy is authoritative.
