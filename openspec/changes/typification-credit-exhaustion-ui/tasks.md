## 0. Grounding (done) — spec corrected to real Web seams

- [x] 0.1 Real type: `AiCreditsResponse` has `consumedCredits` / `allowanceCredits` (null = unlimited) / `remainingCredits` / `usagePercent` (server-computed) / `actionOnExhaustion: string` / `periodEnd`. Spec's `usedCredits`/`totalCredits` do NOT exist.
- [x] 0.2 Real i18n namespace is `typification.aiCredits.*` (NOT `typification.credits.*`); parity baseline is **es-419**; gate = `npm run i18n:check` (also inside `npm run lint`).
- [x] 0.3 Real `actionOnExhaustion` values = `QuotaAction` names `Warn` / `SoftBlock` / `HardBlock` (server default `Warn`); the Web test mock `'Block'` is a bogus placeholder.

## 1. Component — Surface actionOnExhaustion and near-exhaustion warning

- [ ] 1.1 Render an `actionOnExhaustion` badge in `ai-credits-readout.tsx` mapping `Warn`/`SoftBlock`/`HardBlock` → `t('admin:typification.aiCredits.actionOnExhaustion.<warn|softBlock|hardBlock>')`, with distinct visual treatment per value and a graceful fallback (raw value) for an unrecognised string. Stable `data-testid` (e.g. `llm-ai-credits-action`).
- [ ] 1.2 Near-exhaustion warning band: show when `data.usagePercent >= 80 && data.allowanceCredits !== null`, hidden otherwise (and always hidden when unlimited). Stable `data-testid` (e.g. `llm-ai-credits-near-exhaustion`); message `t('admin:typification.aiCredits.nearExhaustion', { percent: Math.round(data.usagePercent) })`.
- [ ] 1.3 Use the existing `useTranslation(['admin'])` + `admin:` prefix pattern; follow @base-ui/react render-prop convention; reuse an existing Badge/Alert primitive if present, else match the in-repo tailwind idiom.

## 2. Internationalisation — parity across all three locales (under typification.aiCredits)

- [ ] 2.1 Add to **es-419** (baseline first) `admin.json` under `typification.aiCredits`: `actionOnExhaustion.label` = "Al agotarse", `actionOnExhaustion.warn` = "Aviso", `actionOnExhaustion.softBlock` = "Bloqueo parcial", `actionOnExhaustion.hardBlock` = "Bloqueo total", `nearExhaustion` = "Has usado el {{percent}} % de tu asignación de créditos."
- [ ] 2.2 Add matching keys to **en-US** `admin.json`: label "On exhaustion", warn "Warn", softBlock "Soft block", hardBlock "Hard block", nearExhaustion "You've used {{percent}}% of your credit allowance."
- [ ] 2.3 Add matching keys to **pt-BR** `admin.json`: label "Ao esgotar", warn "Aviso", softBlock "Bloqueio parcial", hardBlock "Bloqueio total", nearExhaustion "Você usou {{percent}}% da sua cota de créditos."
- [ ] 2.4 Keep identical key SETS across all three (parity gate is bidirectional vs es-419).

## 3. Tests (vitest)

- [ ] 3.1 Near-exhaustion band visible when `usagePercent >= 80` (+ finite allowance); absent below 80; absent when unlimited (`allowanceCredits === null`). Assert via the band `data-testid`.
- [ ] 3.2 actionOnExhaustion badge renders the correct i18n label for each of `Warn`/`SoftBlock`/`HardBlock`. Use the existing test harness (mock `useAiCredits` data, i18n provider). Prefer `data-*` selectors over text where the existing tests do.

## 4. Verification

- [ ] 4.1 `npx vitest run` green (new + existing).
- [ ] 4.2 `npm run build` clean — zero TS errors.
- [ ] 4.3 `npx eslint .` clean.
- [ ] 4.4 `npm run i18n:check` passes — es-419 / en-US / pt-BR identical key sets.
