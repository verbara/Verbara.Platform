# Pro Licensing research — moved to Verbara.Sdk.Pro

**Breadcrumb file.** Los 2 research docs originalmente escritos en este folder fueron movidos al Pro repo (privado) el 2026-05-17:

| Original location | New location |
|---|---|
| `Verbara.Platform/docs/research/2026-05-17-pro-image-binding-independence-from-licensing-mode.md` | `Verbara.Sdk.Pro/docs/research/2026-05-17-pro-image-binding-independence-from-licensing-mode.md` |
| `Verbara.Platform/docs/research/2026-05-17-camino-2-eliminar-licensing-mode.md` | `Verbara.Sdk.Pro/docs/research/2026-05-17-camino-2-eliminar-licensing-mode.md` |

## Razones del move

1. **Research-next-to-code:** el 95% del impacto técnico es en Pro repo (`LicenseOptions`, `LicenseGuard`, `LicenseValidator`, `AddPro*()` extensions, `ContainerImageDigest`). El research debe vivir donde vive el código.
2. **Competitive intel:** el threat model documentado (TA-Disabled-Loophole) describe un vector de "Pro free sin pagar". Pro es repo privado — preserva el incentivo legítimo (license trial gratis vía verbara-website).
3. **Precedente establecido:** `Verbara.Sdk.Pro/docs/research/2026-05-09-pro-image-binding-research.md` (que produjo Pro/ADR-0011) ya estaba en Pro. La regla es "research de Licensing va en Pro".
4. **ADR-0016 transparency preservada:** cuando Pro v2.4.0-pro shipee, se creará un Platform-side plan en `Verbara.Platform/docs/plans/active/2026-XX-XX-platform-v24x-pro-licensing-migration.md` (público) que documente la consumer migration. Eso es lo que el operator final ve, sin exposer el threat model interno.

## Para leer la decisión canónica (cuando esté disponible)

- **Pro ADR-0012** (private): `Verbara.Sdk.Pro/docs/decisions/0012-eliminate-enforcement-mode-for-license-required-model.md`
- **Pro Spec v2.4.0-pro** (private): `Verbara.Sdk.Pro/docs/specs/2026-05-17-pro-v240-licensing-simplification-transition.md`
- **Pro Spec v2.5.0-pro** (private): `Verbara.Sdk.Pro/docs/specs/2026-05-17-pro-v250-licensing-enforcement-mode-removal.md`
- **Pro Plans** (private): `Verbara.Sdk.Pro/docs/plans/active/2026-05-17-pro-v240-execution-plan.md` + `2026-05-17-pro-v250-execution-plan.md`

## Status del trabajo

- **2026-05-17:** research escritos + decisión Camino 2 adoptada + specs/plans/ADR creados en Pro.
- **Próximo step:** ejecutar Pro v2.4.0-pro plan (~25-31h) cuando se abra el train.
- **Después:** Pro v2.5.0-pro (~9h, ≥6 semanas post-v2.4.0).
- **Final step:** Platform-side migration plan + consumer changes (separate plan en este repo).
