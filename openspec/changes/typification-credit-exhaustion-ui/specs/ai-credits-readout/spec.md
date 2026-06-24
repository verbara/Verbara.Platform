## ADDED Requirements

### Requirement: Display actionOnExhaustion policy
The AI credits readout component SHALL render the server-reported `actionOnExhaustion` value (Warn / SoftBlock / HardBlock) as a visually distinct badge so tenants know what will happen when credits are exhausted.

#### Scenario: Badge shows Warn policy
- **GIVEN** the server returns `actionOnExhaustion: "Warn"` on `AiCreditsResponse`
- **WHEN** the `ai-credits-readout` component renders
- **THEN** it displays an i18n-keyed label identifying the Warn exhaustion policy

#### Scenario: Badge shows SoftBlock policy
- **GIVEN** the server returns `actionOnExhaustion: "SoftBlock"`
- **WHEN** the `ai-credits-readout` component renders
- **THEN** it displays an i18n-keyed label identifying the SoftBlock exhaustion policy

#### Scenario: Badge shows HardBlock policy
- **GIVEN** the server returns `actionOnExhaustion: "HardBlock"`
- **WHEN** the `ai-credits-readout` component renders
- **THEN** it displays an i18n-keyed label identifying the HardBlock exhaustion policy

### Requirement: Near-exhaustion warning band
The AI credits readout component SHALL display a warning band when credit usage is at or above 80 % of the tenant quota, giving operators advance notice before enforcement activates.

#### Scenario: Warning band visible at threshold
- **GIVEN** `usedCredits / totalCredits >= 0.80`
- **WHEN** the `ai-credits-readout` component renders
- **THEN** a near-exhaustion warning element is visible with an i18n-keyed message

#### Scenario: Warning band absent below threshold
- **GIVEN** `usedCredits / totalCredits < 0.80`
- **WHEN** the `ai-credits-readout` component renders
- **THEN** no near-exhaustion warning element is rendered

### Requirement: i18n parity for credit exhaustion strings
All new UI strings for exhaustion policy labels and the near-exhaustion warning SHALL be present in EN-US, ES-419, and PT-BR locale files. The CI i18n parity gate MUST pass with no missing keys.

#### Scenario: All locales define exhaustion keys
- **GIVEN** the feature is built and locale files are updated
- **WHEN** the i18n parity check runs in CI
- **THEN** EN-US, ES-419, and PT-BR all contain identical key sets for the new strings with zero missing-key failures
