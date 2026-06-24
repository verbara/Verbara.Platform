## 1. Component — Surface actionOnExhaustion and near-exhaustion warning

- [ ] 1.1 Add `actionOnExhaustion` label badge to `ai-credits-readout` component, rendering Warn / SoftBlock / HardBlock with distinct visual treatment
- [ ] 1.2 Implement near-exhaustion warning band: show when `usedCredits / totalCredits >= 0.80`, hide otherwise
- [ ] 1.3 Wire i18n keys for badge labels and warning copy; use existing `useTranslation` pattern

## 2. Internationalisation — Parity across all three locales

- [ ] 2.1 Add `typification.credits.actionOnExhaustion.warn`, `.softBlock`, `.hardBlock` keys to EN-US `admin.json`
- [ ] 2.2 Add matching keys to ES-419 `admin.json`
- [ ] 2.3 Add matching keys to PT-BR `admin.json`
- [ ] 2.4 Add `typification.credits.nearExhaustion.warning` key to all three locales

## 3. Tests

- [ ] 3.1 Add vitest cases for `ai-credits-readout`: warning band appears at >= 80 % usage and is absent below threshold
- [ ] 3.2 Add vitest cases: actionOnExhaustion badge renders correct label for each enum value (Warn, SoftBlock, HardBlock)

## 4. Verification

- [ ] 4.1 `npx vitest run` green (new and existing tests)
- [ ] 4.2 `npm run build` clean — zero TypeScript errors, zero lint warnings
- [ ] 4.3 `npx eslint .` clean
- [ ] 4.4 i18n parity gate passes (`i18n:check` or equivalent CI step) — EN-US / ES-419 / PT-BR all match
