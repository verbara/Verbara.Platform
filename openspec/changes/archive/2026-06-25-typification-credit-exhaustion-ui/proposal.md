---
tier: PEQUEÑO
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

The AI credits readout (C5 component) displays raw credit consumption figures but never surfaces the server-reported `actionOnExhaustion` policy (Warn / SoftBlock / HardBlock) or a near-exhaustion warning, so a tenant has no UI signal before SoftBlock silently degrades suggestions or HardBlock returns a 402. The data is already present on `AiCreditsResponse`; only the presentation layer is missing.

## What Changes

- Render the `actionOnExhaustion` value (Warn / SoftBlock / HardBlock) as a labelled badge inside the `ai-credits-readout` component.
- Show a near-exhaustion warning band when usage is at or above 80 % of the quota, matching the server-side Warn threshold.
- Add i18n keys for the new strings in all three required locales: EN-US, ES-419, PT-BR.

## Capabilities

### New Capabilities

- `ai-credits-readout`: UI presentation requirements for surfacing the server-reported exhaustion policy and a near-exhaustion warning band in the AI credits readout component.

### Modified Capabilities

_(none — `AiCreditsResponse.actionOnExhaustion` already exists on the server; no API requirement changes)_

## Impact

- **Verbara.Platform.Web** — `ai-credits-readout` component and its vitest suite; `use-typification-llm` type definitions (no change required, field already typed); `admin.json` locale files (EN-US, ES-419, PT-BR).
- No API change; no Platform (.NET) change; no SDK / Pro change.
