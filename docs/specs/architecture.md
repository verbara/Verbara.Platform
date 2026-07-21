# Architecture — Verbara.Platform

> The API host and composition root of the Verbara omnichannel contact center — the **leaf** of the
> `Verbara.Sdk → Verbara.Sdk.Pro → Verbara.Platform ← Verbara.Platform.Web` chain. Public
> repository; the crown-jewel closed-source Pro IP it consumes ships **only** as Native AOT
> machine code, never as decompilable IL.

## 1. Role & boundaries

Verbara.Platform is the executable that *assembles* a contact center from library packages. It owns:

- The **composition root** (`src/Verbara.Platform.Api/Program.cs`) — DI registration, dual-scheme
  auth, RBAC, the middleware pipeline, and the `/api/v1/` endpoint surface (~86 endpoint files).
- The **domain packages** it defines itself (Conversations, Queues, Switchboard, Channels, Flows,
  Typification, Billing, …) and its **storage adapters** (InMemory for dev/test, PostgreSQL for
  production).
- Three **out-of-process microservices** (Realtime, Renderer, Mail) it hosts alongside the API.

It must **not** reach around the chain. Asterisk/AMI/ARI/AGI, the Voice-AI pipeline, resilience
primitives and session plumbing belong to **Verbara.Sdk** (MIT, upstream); event sourcing,
predictive dialer, analytics, agent-assist, clustering, multi-tenant licensing belong to
**Verbara.Sdk.Pro** (private). Platform consumes both as NuGet packages pinned in
`Directory.Packages.props` — it never vendors or forks them. The React frontend
(**Verbara.Platform.Web**) is API-first and decoupled: Platform defines the contract (OpenAPI, DTOs);
Web consumes it. Web-behavior specs are authored *here* (this repo is the authoritative workstream
for Platform + Platform.Web), but frontend source lives in its own repo.

## 2. Architecture style — a modular monolith that ships as a 4-image matrix

The API is a **modular monolith**: 35 `src/` packages, each a single-responsibility library with
exactly one DI extension (`AddConversations()`, `AddQueues()`, …), all wired at one composition root.
There is no per-service database or network hop between domain modules — they share a process and a
single `NpgsqlDataSource` pool (ADR-0015).

But the *deployable* unit is a **4-image matrix** (ADR-0023), split by AOT-eligibility, not by domain:

| Image | Build | Why | Pro IP |
|---|---|---|---|
| **api** | **Native AOT** | crown-jewel closed-source Pro logic must not ship as readable IL | carries it |
| **realtime** | IL | SignalR server dispatch can't be Native AOT | non-crown-jewel plumbing only (guarded) |
| **renderer** | IL | QuestPDF / ScottPlot reflection | none |
| **mail** | IL | MailKit / MS Graph reflection | none |

The three IL images exist **because** their dependencies are AOT-hostile — they are IL *by design*,
not by omission, and are structurally forbidden from carrying crown-jewel Pro packages (guard in §4).
All four are pushed public and cosign-signed on every release; only the **api** image's digest feeds
the Layer-C authorized-digests license binding.

Inbound flow is a pipeline: `IWebhookHandler → IInboundMessagePipeline (dedup → contact → conversation
→ persist) → IInboundRouter → IConversationSwitchboard`. Storage is conditional — a present
`ConnectionStrings:Postgres` activates Postgres-backed stores; its absence falls back to InMemory
singletons (drop-in dev/test default).

## 3. Design principles (as actually practiced here)

- **DI over service-locator.** Dependencies are constructor-injected from the composition root.
  `RequestServices.GetService<T>()` inside an endpoint is an anti-pattern — an architecture test
  (`ServiceLocatorScanner`) allows *exactly one* documented survivor and fails on any new one (§5).
- **One responsibility per package, one DI extension per package.** Adding a capability means a new
  `src/` package with its own `Add…()` — not another branch in a god-file. The composition root has a
  frozen LOC budget precisely to force extraction over growth.
- **No reflection — source-gen everything (ADR-0022).** JSON via `[JsonSerializable]` contexts
  (`ApiJsonContext`, `RealtimeContractsJsonContext`); logging via `[LoggerMessage]`; request delegates
  via `EnableRequestDelegateGenerator`. No `Activator.CreateInstance`, no dynamic proxies. The api host
  sets `JsonSerializerIsReflectionEnabledByDefault=false`, so an un-registered DTO fails at build.
- **Reflection-free Npgsql, name-based mapping (NO Dapper).** Data access goes through the
  `Verbara.Sdk.Data.Npgsql` facade (`NpgsqlExecutor`). Row types are classes with `{ get; init; }`
  and a hand-written `static Map(NpgsqlDataReader)` using name-based getters; parameters bind
  explicitly via `NpgsqlParameter` (no anonymous objects). Every nullable param that can be
  `DBNull.Value` sets an explicit `NpgsqlDbType` (else Postgres `42P08`). One shared `NpgsqlDataSource`
  — never `new NpgsqlConnection` directly.
- **Typed DTOs only.** Endpoints return sealed records registered in a `[JsonSerializable]` context —
  never anonymous `new { }` (which reflection-serializes and breaks AOT).
- **Credentials come from a CSPRNG, not a GUID.** Secrets/keys/tokens mint through
  `SecretTokenGenerator.Mint` (CSPRNG-32). A `Guid.NewGuid()` is unique, not unguessable (~122 bits) —
  interpolating one into a credential-named value is a gated finding (§5).
- **Errors are never silently swallowed.** An empty `catch {}` under any `Endpoints/` directory is
  forbidden; a best-effort swallow must carry a body that logs the defer, keeping the
  eventual-consistency contract visible.
- **Order invariants are load-bearing.** The middleware pipeline order is contractual:
  `TenantResolution` MUST precede `UseRateLimiter()` (else every request collapses to the shared
  partition — the v2.14.1 bug; ADR-0031), which MUST precede `UseAuthentication()`.

## 4. Constraints & banned dependencies

- **Native AOT is non-negotiable for the api image (ADR-0022).** Every shippable api build is Native
  AOT with zero `IL2026`/`IL3050`/`IL207x` diagnostics — this is the mechanism that keeps closed-source
  Pro IP from shipping as decompilable IL. `Directory.Build.props` sets `IsAotCompatible`,
  `EnableTrimAnalyzer`, `EnableAotAnalyzer` repo-wide.
- **Dapper is permanently banned (ADR-0022).** Dapper / Dapper.AOT / `Verbara.Sdk.Dapper.Stubs` rely on
  `DynamicMethod` + `MakeGenericType` (runtime IL emit) and block AOT. The `BanDapperPackageReferences`
  MSBuild target in `Directory.Build.props` **fails the build** on any reference. Use
  `Verbara.Sdk.Data.Npgsql`.
- **Crown-jewel Pro packages are banned from the IL microservices (ADR-0023).** The
  `BanCrownJewelProInNonAotMicroservices` target fails the build if Realtime/Renderer/Mail reference
  `Verbara.Sdk.Pro.{Dialer,Analytics,CallAnalytics,AgentAssist,EventStore,Routing}` — that logic stays
  in the AOT api image, where it can't be decompiled.
- **`TreatWarningsAsErrors=true`, `WarningLevel=9999`, `Nullable=enable`, C# latest, .NET 10** across
  all ~40 projects. Zero warnings tolerated; nullable reference types enforced.

## 5. The Gate Contract — the heart

Values become build-blocking checks. Each invariant below maps to a concrete gate with a real
implementation. This table is the human-readable face of **`gates.yaml`** (the machine-checkable
manifest, cross-checked by verbara-meta `/xr:doctor`), keyed to the ADR-0014 §2 gate classes.

| Invariant (principle) | Enforcing gate | CI job / script |
|---|---|---|
| Compiles clean, unit tests green (G1) | Release build + `dotnet test` over `.slnx` | `Build + Unit Tests (Release)` (`ci.yml`) |
| Zero warnings + no banned deps (G2) | `TreatWarningsAsErrors` + `BanDapperPackageReferences` + `BanCrownJewelProInNonAotMicroservices` | `Directory.Build.props` (fails the Release build) |
| Coverage doesn't regress (G3) | ADR-0013 triplet: patch cover, two-sided band, exclusion baseline | `Coverage Ratchet` (`check-patch-coverage.py`, `check-coverage-floor.py`, `check-exclusion-baseline.py`) |
| No service-locator; no N+1 enrichment loops (G4) | `Architecture.Tests` (`ServiceLocatorScanner`, `EnrichmentLoopScanner`) + `Governance.Tests` (`SyncFenceScanner`) — Roslyn scanners with liveness self-tests | run in the unit lane (`.slnx`) — `Build + Unit Tests` / `Coverage Ratchet` |
| No empty `catch{}`; credentials from a CSPRNG (G5) | `check-endpoint-invariants.py` (empty-catch gate #6, Guid-mint gate #7) | `Invariant Gates` (required check) |
| Composition root stays bounded (G6) | `LOC_BUDGETS` frozen ratchet in the same script (gate #9) | `Invariant Gates` (required check) |
| The api image really AOT-publishes (G7) | real `PublishAot` publish, fail on any `warning IL…` | `AOT Publish (Api)` (required check) — **scoped to the api image; the 3 IL images are N/A-for-AOT by design (§2)** |
| No vulnerable / copyleft deps (G8) | Dependabot + Dependency Review + CodeQL + cosign-signed images + digest reconciliation | `Dependency Review`, `CodeQL`, `release.yml`, `digest-reconciliation` |

The gates are **freeze-current ratchets**: they fail on *regression*, not on a green-field ideal.
Budgets ratchet down as orchestrator files shrink; the Guid-mint / empty-catch floors are zero because
the pre-existing violations were remediated in the change that introduced each gate.

## 6. Testing conventions

- **xUnit + FluentAssertions**, naming `Method_ShouldExpected_WhenCondition`.
- **Unit vs integration by *project*, not `[Category]` trait.** The fast lane excludes exactly the two
  genuinely container-backed projects (`Storage.Postgres.Tests`, `Identity.Redis.Tests`); a separate
  `Live-DB Tests (Postgres)` lane runs them against **Testcontainers** Postgres/Redis (report-only
  pending a fixture-level connect-retry fix).
- **Architecture self-tests carry liveness assertions.** Every scanner
  (`ServiceLocatorScanner`, `EnrichmentLoopScanner`, `SyncFenceScanner`) asserts it walked a minimum
  file count — a broken locator can never present as a false green — and pins its own true/false
  positives with unit tests. The `SyncFence` guard forbids adding a wall-clock synchronization barrier
  to a test without an inline `// fence-allow:` marker.
- **Coverage-gate scripts are themselves unit-tested** (`Coverage Script Tests` job,
  `scripts/tests/`).
- **E2E** (in Platform.Web) selects on locale-proof `data-*` attributes, never on visible text.

## 7. Where decisions live

- **ADRs:** `docs/decisions/` (append-only). Load-bearing entries: ADR-0022 (AOT + Dapper ban),
  ADR-0023 (4-image AOT/IL split), ADR-0015 (shared `NpgsqlDataSource`), ADR-0031 (rate-limiter after
  tenant resolution), ADR-0025 (liveness/readiness contract), ADR-0035 (OpenAPI typed-client contract).
- **Gate-class contract:** verbara-meta **ADR-0014** (§1 charter prose = this file; §2 gate manifest =
  `gates.yaml`); coverage mechanism = verbara-meta ADR-0013; AOT-at-PR gate = verbara-meta ADR-0012.
- **Living specs / backlog:** `openspec/` (open changes ARE the backlog).
- **Day-to-day contributor guidance:** `CLAUDE.md` (+ path-scoped `.claude/rules/`) and the
  `platform-fullstack-expert` agent (`.claude/agents/`).
