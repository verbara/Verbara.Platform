## ADDED Requirements

### Requirement: Display actionOnExhaustion policy
The AI credits readout component (`ai-credits-readout.tsx`) SHALL render the server-reported `AiCreditsResponse.actionOnExhaustion` value as a visually distinct badge so tenants know what will happen when credits are exhausted. The server emits the `QuotaAction` enum name as a bare string — one of `Warn`, `SoftBlock`, `HardBlock` (default `Warn`). The badge SHALL map each known value to an i18n-keyed label under `typification.aiCredits.actionOnExhaustion.*`, and SHALL degrade gracefully (render the raw value, no crash) for any unrecognised string.

#### Scenario: Badge shows Warn policy
- **GIVEN** the server returns `actionOnExhaustion: "Warn"` on `AiCreditsResponse`
- **WHEN** the `ai-credits-readout` component renders
- **THEN** it displays a badge with the i18n label `typification.aiCredits.actionOnExhaustion.warn`

#### Scenario: Badge shows SoftBlock policy
- **GIVEN** the server returns `actionOnExhaustion: "SoftBlock"`
- **WHEN** the `ai-credits-readout` component renders
- **THEN** it displays a badge with the i18n label `typification.aiCredits.actionOnExhaustion.softBlock`

#### Scenario: Badge shows HardBlock policy
- **GIVEN** the server returns `actionOnExhaustion: "HardBlock"`
- **WHEN** the `ai-credits-readout` component renders
- **THEN** it displays a badge with the i18n label `typification.aiCredits.actionOnExhaustion.hardBlock`

### Requirement: Near-exhaustion warning band
The AI credits readout component SHALL display a warning band when the server-computed `usagePercent` is at or above 80 AND the plan is not unlimited (`allowanceCredits !== null`), giving operators advance notice before enforcement activates. The band uses the i18n-keyed message `typification.aiCredits.nearExhaustion` (interpolating the rounded percent). An unlimited plan has no exhaustion and SHALL never show the band.

#### Scenario: Warning band visible at threshold
- **GIVEN** `AiCreditsResponse.usagePercent >= 80` and `allowanceCredits !== null`
- **WHEN** the `ai-credits-readout` component renders
- **THEN** a near-exhaustion warning element (identified by a stable `data-testid`) is visible with the i18n-keyed message

#### Scenario: Warning band absent below threshold
- **GIVEN** `AiCreditsResponse.usagePercent < 80`
- **WHEN** the `ai-credits-readout` component renders
- **THEN** no near-exhaustion warning element is rendered

#### Scenario: Warning band absent for unlimited plan
- **GIVEN** `allowanceCredits === null` (unlimited), regardless of `usagePercent`
- **WHEN** the `ai-credits-readout` component renders
- **THEN** no near-exhaustion warning element is rendered

### Requirement: i18n parity for credit exhaustion strings
All new UI strings (the three `actionOnExhaustion` labels, an optional badge label, and the near-exhaustion message) SHALL live under the existing `typification.aiCredits.*` namespace in EN-US, ES-419, and PT-BR `admin.json`. The CI i18n parity gate (`scripts/i18n-parity-check.mjs`, baseline **es-419**) MUST pass with identical key sets across all three locales.

#### Scenario: All locales define exhaustion keys
- **GIVEN** the feature is built and the three `admin.json` files are updated under `typification.aiCredits`
- **WHEN** `npm run i18n:check` runs in CI
- **THEN** ES-419 (baseline), EN-US, and PT-BR all contain identical key sets for the new strings with zero missing/extra drift

## Architectural Risk

**Level:** LOW

**Affected:** `Verbara.Platform.Web` only — `src/admin/typification/llm/ai-credits-readout.tsx`, its vitest suite, and `public/locales/{en-US,es-419,pt-BR}/admin.json`. No API change (`AiCreditsResponse.actionOnExhaustion` + `usagePercent` already exist and are typed), no .NET/SDK/Pro change.

**Mitigation:** Presentation-only. Reuses the existing server fields (`usagePercent`, `actionOnExhaustion`) — no new request, no hook change. The badge tolerates unknown `actionOnExhaustion` strings (the type is a bare `string`). i18n parity is enforced by the existing CI gate, so a missing locale key fails the build rather than shipping silently. @base-ui/react render-prop convention (no Radix/asChild) preserved.
