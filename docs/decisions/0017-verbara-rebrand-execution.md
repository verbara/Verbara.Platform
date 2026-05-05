# ADR-0017: Verbara Rebrand Execution — Versioning and Scope Decisions

- **Status:** Accepted
- **Date:** 2026-05-05
- **Deciders:** Verbara maintainer (Harol A. Reina H.)
- **Related:** [ADR-0016 License and Rebrand to Verbara](0016-license-and-rebrand-to-verbara.md)

## Context

ADR-0016 decided to rebrand from "Asterisk.*" to "Verbara.*" and license Platform under Apache 2.0. This ADR documents the execution decisions made during the rebrand implementation.

## Decisions

### D1: Version Strategy — 2.0.0 Major Bump

All repositories receive a major version bump:
- `Verbara.Sdk` → 2.0.0
- `Verbara.Sdk.Pro` → 2.0.0-pro
- `Verbara.Platform` → 2.0.0
- `verbara-web` → 2.0.0

**Rationale:** Breaking namespace change is semver-correct as a major bump. Clean slate for public launch. No backward compatibility obligations (pre-launch, zero external consumers).

### D2: Single Unified Plan with Pre-Alignment

Executed as one coordinated plan in dependency order (SDK → Pro → Platform → Web) rather than separate per-repo plans. Pre-alignment step bumped SDK pin from 1.15.1 → 1.15.3 in both Pro and Platform before the rename.

**Rationale:** Partial rebrand creates inconsistent state. Tight coupling between repos makes independent execution risky. Pre-alignment eliminates version gap as a confounding variable during rename.

### D3: Configuration Section "Asterisk" Preserved

The .NET configuration section `"Asterisk:Ami"` / `"Asterisk:Ari"` is KEPT unchanged. Environment variables `Asterisk__Ami__Hostname`, `Asterisk__Ari__BaseUrl`, etc. remain as-is.

**Rationale:** The config section names the TARGET TECHNOLOGY (Asterisk PBX), not our product. This follows industry convention: StackExchange.Redis uses config section "Redis", Npgsql uses "PostgreSQL". Our SDK connects TO Asterisk PBX → the section `"Asterisk"` is semantically correct and self-documenting for operators.

### D4: Database Name Standardized to "verbara"

- Docker compose: `Database=platform` → `Database=verbara`
- K8s CloudNativePG: `asterisk_platform` → `verbara`
- Loadtest: `asterisk_loadtest` → `verbara_loadtest`

**Rationale:** No production databases exist with real data (pre-launch). Config-only change with zero migration risk. For the commercial product, the DB name should unambiguously identify the product.

### D5: ARI Application Name → "verbara"

The Asterisk ARI Stasis application name changed from `asterisk_platform` / `asterisk-platform` (previously inconsistent) to `verbara` across:
- `extensions.conf` Stasis() calls
- Docker compose `Asterisk__Ari__Application` env var
- K8s helm values
- `ari.conf` user section `[verbara]`

**Rationale:** The ARI application name is how Asterisk PBX routes WebSocket events to our platform. It should identify our product unambiguously. The previous name was also inconsistent (underscore vs hyphen).

### D6: PBX Domain Types Preserved

Types representing Asterisk PBX domain concepts are NOT renamed:
- `AsteriskChannel`, `AsteriskBridge`, `AsteriskQueue`, `AsteriskAgent`, `AsteriskQueueMember`
- `AsteriskVersion` properties (PBX version reporting)
- `AsteriskAmiHealthCheck` (PBX connectivity check)
- `AriAsteriskResource`, `AriAsteriskInfo`, `AriAsteriskPing` (ARI REST API resources)
- `AsteriskContainer*` test fixtures (PBX test container)

**Rationale:** These types model the Asterisk PBX domain, not our product. Renaming `AsteriskChannel` to `VerbaraChannel` would be semantically incorrect — it IS an Asterisk channel.

### D7: Package Validation Disabled

`EnablePackageValidation` disabled in SDK during rebrand. No baseline packages exist under `Verbara.Sdk.*` on any feed.

**Action item:** Re-enable after first stable Verbara.Sdk publish with `PackageValidationBaselineVersion=2.0.0`.

### D8: GitHub Org Transfer Deferred

Code rename shipped first. GitHub org transfer (`github.com/verbara`) is a separate operational step after 1-2 days of local validation.

## Consequences

- 4 repositories renamed simultaneously — total ~3,900 files modified
- All 50 NuGet packages now publish under `Verbara.*` prefix
- Local NuGet feed rebuilt with exclusively `Verbara.*` packages
- Extension methods provide a clean API surface: `services.AddVerbara(configuration)`
- Operators see `Asterisk__Ami__*` env vars and immediately understand: "configures the PBX connection"
- MFA authenticator apps show "Verbara" as the issuer (user-facing)
- Scalar/OpenAPI UI shows "Verbara Platform API"
- Zero breaking changes to PBX interaction layer (config, protocol, domain model)
