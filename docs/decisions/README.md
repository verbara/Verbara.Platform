# Architecture Decision Records (ADRs)

Append-only log of load-bearing architectural decisions — the **why**, not the **how**.

## When to add an ADR

Write an ADR when a decision:

- Constrains or shapes future work.
- Was debated (multiple options evaluated, one chosen).
- Would be surprising to a new engineer reading the code 6 months from now.
- Rules out a path that might look attractive later ("why don't we just…?").

Do **not** write an ADR for obvious or trivial choices; that's what code and commit messages are for.

## File convention

`{NNNN}-{kebab-case-title}.md` — sequential 4-digit prefix, starting at `0001`.

Status values: `Proposed` · `Accepted` · `Superseded by ADR-XXXX` · `Deprecated`.
Once `Accepted`, never edit the body — supersede with a new ADR that references this one.

## Template

```markdown
# ADR-NNNN: {Title}

- **Status:** Proposed | Accepted | Superseded by ADR-XXXX
- **Date:** YYYY-MM-DD
- **Deciders:** {names or role}
- **Related:** ADR-XXXX, spec file, plan file

## Context
What problem are we solving? What forces / constraints are in play?

## Decision
The decision, stated in one or two sentences.

## Consequences
- Positive: …
- Negative: …
- Neutral / trade-off: …

## Alternatives considered
- **Option B:** … — rejected because …
- **Option C:** … — rejected because …
```

## Catalog

<!-- Add one line per ADR as they are created -->
