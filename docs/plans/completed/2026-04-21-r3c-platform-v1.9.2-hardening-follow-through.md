# Plan — R3c Platform v1.9.2 "Hardening Follow-Through" (skeleton)

## Context

Baseline: Platform **v1.9.1** "Resilience Coverage" shipped 2026-04-21 (commit `496365a`, tag `v1.9.1`). SDK 1.15.0 + Pro 1.10.0-pro pinned. 1,733 unit tests green / 0 warnings.

v1.9.0 y v1.9.1 cerraron P0 security (impersonation + MFA 4-way) + observability foundation + resilience coverage horizontal (29 call-sites). Pero las auditorías de v1.9.0 surfacearon **5 concerns orthogonales** explícitamente diferidos a v1.9.2. La exploración 2026-04-21 confirma que ninguno fue silently shippeado; los 5 siguen abiertos y son el scope natural del próximo patch.

v1.9.2 NO introduce features ni cambia API surface. Es un hardening release que cierra la deuda residual de seguridad + compatibilidad Asterisk antes de pasar a R4 o a v2.0-preview1. Se shippea **en paralelo a R4 Platform.Web** sin bloqueo (zero API surface changes).

### 5 frentes (scope verificado)

| # | Frente | Gap | Tamaño | Paralelizable con |
|---|--------|-----|--------|-------------------|
| A | Asterisk 23 Standard matrix | `docker/Dockerfile.asterisk` hardcoded `andrius/asterisk:22` + codec_opus 22.0 URL; no `ASTERISK_VERSION` build-arg; no CI matrix | M | Todos (independiente) |
| B | JWT hardening | `jti` missing en access + impersonation tokens; signing key XML plaintext sin rotation; `kid` hardcoded; `?token=` query-string fallback en `ApiKeyAuthenticationHandler` | M-L | A, D (C toca mismo archivo) |
| C | OIDC callback MFA enforcement | `OidcEndpoints.CompleteOidcLoginAsync` salta `RequiresMfaForUserAsync`; helper ya existe en `AuthEndpoints.cs:852` (v1.9.0) pero es `internal static` | M | A, B, D |
| D | ChangePassword MFA step-up | `AuthEndpoints.cs:387-428` no valida MFA antes de cambiar password; stolen session cookie → silent password change | M | A, C (no paralelo con B por colisión archivo) |
| E | MFA cache cross-instance consistency | `MfaPendingCache` + `PasswordResetCache` son `ConcurrentDictionary` estáticas en `AuthEndpoints.cs:28-29`; MFA challenges se pierden en failover multi-instancia | L | A (serial con B/C/D por colisión archivo) |

### Reuse opportunities identificadas

- `DataProtection` ya registrado en `Program.cs:380` — usable para cifrar RSA signing key en lugar de XML plaintext (Frente B).
- `IConnectionMultiplexer` disponible vía `Asterisk.Sdk.Cluster.Redis` o `Asterisk.Sdk.Pro.Push` backplane — reusable para `RedisMfaPendingCache` (Frente E).
- `RequiresMfaForUserAsync` helper ya shippado en v1.9.0 — sólo hay que extraerlo a servicio injectable (Frente C reuse).
- Patrón `MfaPendingEntry` + `/mfa/verify` endpoint ya establecido en v1.9.0 — reutilizable para step-up challenge (Frente D).

## Approach

### Execution order (parallelization map)

**Paralelo (Phase 1 — ~3 subagents):**
- **Subagent 1 — Frente A (Asterisk 23):** parametrizar Dockerfile + docker-compose + GH Actions matrix job. Self-contained, no colisiona con ningún otro frente.
- **Subagent 2 — Frente B (JWT):** `JwtTokenService.cs` + `ApiKeyAuthenticationHandler.cs` + `Program.cs:369` key loader + nuevo `IJtiRevocationCache` interface. No colisiona con Frente C/D (touches distintos archivos aunque relacionados conceptualmente).
- **Subagent 3 — Frente C (OIDC MFA):** extract `RequiresMfaForUserAsync` a `IMfaPolicyEvaluator` service; apply en `OidcEndpoints.CompleteOidcLoginAsync:170` antes de token issuance. Touches `OidcEndpoints.cs` + new service interface en `Platform.Identity`.

**Serial (Phase 2):**
- **Frente D (ChangePassword MFA step-up):** depende de `IMfaPolicyEvaluator` de Frente C (reuse). Touches `AuthEndpoints.cs:387-428`. Serial porque también toca AuthEndpoints.cs junto con Frente E.
- **Frente E (MFA cache backplane):** LARGE — crear `IMfaPendingCache` + `IPasswordResetCache` interfaces en `Platform.Identity` + in-memory impl (default, backward-compat) + Redis impl. Registrar en DI. Reemplazar uso directo de `ConcurrentDictionary` en `AuthEndpoints.cs:22,28-29`. Serial con D para evitar merge conflicts.

**Phase 3 — release hygiene:**
- Version bump 1.9.1 → 1.9.2 en `Directory.Build.props`.
- CHANGELOG.md sección v1.9.2 (Security + Changed + Added subsections).
- Tag `v1.9.2` + GitHub Release (con confirmación explícita del usuario antes de push).
- Mover plan a `docs/plans/completed/`.

### Criterios de "done"

- ✅ `dotnet build Asterisk.Platform.slnx /warnaserror` — 0 warnings.
- ✅ `dotnet test` — ~1,755 tests pass (1,733 baseline + ~22 nuevos across 5 frentes). 0 failures.
- ✅ Asterisk 22 + 23 smoke test vía `docker compose up` (Frente A acceptance).
- ✅ Pen-test checklist Frentes B/C/D (JWT forgery, OIDC MFA bypass, ChangePassword without MFA) — todos 403/401.
- ✅ Multi-instance smoke: MFA challenge iniciado en instancia 1, completado en instancia 2 (Frente E acceptance).
- ✅ Zero API surface changes (DTO compare vs v1.9.1 snapshot).

## Critical files

**Modified:**
- `docker/Dockerfile.asterisk` — parametrizar ASTERISK_VERSION (Frente A)
- `docker/docker-compose.full.yml` — service env var (Frente A)
- `.github/workflows/*.yml` — matrix job 22/23 (Frente A — pendiente auditar si existe workflow)
- `src/Asterisk.Platform.Api/Services/JwtTokenService.cs` — `jti` + DataProtection key (Frente B)
- `src/Asterisk.Platform.Api/Auth/ApiKeyAuthenticationHandler.cs` — remover `?token=` query fallback (Frente B)
- `src/Asterisk.Platform.Api/Program.cs` — key loader + DI wiring nuevo (B, C, E)
- `src/Asterisk.Platform.Api/Endpoints/OidcEndpoints.cs:170` — MFA gate en callback (Frente C)
- `src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs:22,28-29,387-428,852` — cache interfaces + step-up helper (Frentes D, E)

**Created:**
- `src/Asterisk.Platform.Identity/Mfa/IMfaPolicyEvaluator.cs` (Frente C extract)
- `src/Asterisk.Platform.Identity/Mfa/IMfaPendingCache.cs` + in-memory impl (Frente E)
- `src/Asterisk.Platform.Identity/Mfa/IPasswordResetCache.cs` + in-memory impl (Frente E)
- `src/Asterisk.Platform.Api/Mfa/RedisMfaPendingCache.cs` (Frente E — optional, flag-gated)
- `src/Asterisk.Platform.Api/Auth/IJtiRevocationCache.cs` + in-memory impl (Frente B)
- Tests: ~22 new across 5 assemblies (JWT forgery, OIDC MFA, step-up, cache interface contract, Asterisk 23 container smoke)

## Verification

```sh
cd /media/Data/Source/Verbara/Asterisk.Platform

# Build + tests (post cada commit)
dotnet build Asterisk.Platform.slnx --nologo /warnaserror
dotnet test Asterisk.Platform.slnx --filter "FullyQualifiedName!~Postgres" --nologo --no-build

# Frente A — Asterisk 23 matrix
docker build --build-arg ASTERISK_VERSION=22 -f docker/Dockerfile.asterisk -t asterisk-platform:22-test .
docker build --build-arg ASTERISK_VERSION=23 -f docker/Dockerfile.asterisk -t asterisk-platform:23-test .
docker compose -f docker/docker-compose.full.yml up  # smoke ambas versiones

# Frente B — JWT forgery resistance
dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "FullyQualifiedName~Jwt"

# Frente C/D — MFA enforcement
dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "FullyQualifiedName~OidcMfa|ChangePasswordMfa"

# Frente E — cache interface contract
dotnet test tests/Asterisk.Platform.Identity.Tests/ --filter "FullyQualifiedName~MfaCache"
```

## Out of scope (diferidos a releases posteriores)

- **R2 / v2.0-preview1 concerns:** Event Model v2, CloudEvents, IEventLog/IEventStore split — sigue en roadmap SDK.
- **R1.5 SDK v1.15.1 "VoiceAi Refresh":** branch paralela, no toca Platform.
- **WFM / Callback Queue / CSAT:** features tier diferidos a Pro 1.11.x post-R2.
- **SCIM / WebAuthn:** aplazados desde v1.4, tracked en roadmap.

## No hacer en esta iteración

- NO agregar features (callback queue, CSAT, CRM). v1.9.2 es hardening puro.
- NO cambiar API surface (response DTOs, endpoint shapes). Si Frente C/D necesita nueva response, reutilizar `MfaChallengeRequiredResponse` de v1.9.0.
- NO paralelizar Frentes B+D+E (colisión `AuthEndpoints.cs` + `Program.cs`). Phase 1 estrictamente A/B/C, Phase 2 serial D→E.
- NO squash todos los commits — audit trail por frente.
- NO push sin confirmación explícita del usuario (convención establecida).
