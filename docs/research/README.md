# Research

Exploratory findings, market analysis, competitive research, design discovery — work that **informs** specs and plans but is not itself a spec or a plan.

## When to add a research doc

- Competitive analysis of external products / frameworks.
- Deep dive into an unfamiliar codebase or subsystem before proposing a change.
- Benchmarks / feasibility studies.
- Summary of interviews, survey results, or external references.

A research doc answers "what exists / what have others done / what did we learn?" — not "what will we build" (spec) or "how will we build it" (plan).

## Lifecycle

```
research/               ← current findings, referenced by active specs + plans
  └ archived/           ← older research kept for historical context
```

Move a doc to `archived/` when:

- The decisions it informed have shipped long enough ago that the research reads as historical context.
- A newer research doc supersedes it.

Never delete research — it often explains **why** decisions look the way they do years later.

## File convention

`{YYYY-MM-DD}-{topic-kebab}.md` — e.g. `2026-03-29-queue-member-types.md`.

No special front-matter required; start with a one-paragraph TL;DR so future readers can skim.
