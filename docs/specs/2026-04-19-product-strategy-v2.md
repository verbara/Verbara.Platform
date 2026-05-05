# Product Strategy Document v2 — Asterisk Stack

- **Status:** Complete draft (§1-§11 completas, pending user approval + ADR stubs subordinados)
- **Date:** 2026-04-19
- **Owners:** Harold Reina
- **Scope:** cross-repo (`Asterisk.Sdk` MIT · `Asterisk.Sdk.Pro` commercial · `Asterisk.Platform` + `Asterisk.Platform.Web` host)
- **Related:**
  - Análisis técnico de alineación (plan file): `/home/orion75/.claude/plans/antes-de-continuar-quiero-quizzical-gizmo.md`
  - Re-auditoría v1.13.0: mismo plan file §13
  - ADR-0001 (Pro 1.11 adoption): `Asterisk.Sdk.Pro/docs/decisions/0001-sdk-1.11-adoption.md`
  - ADR-0002 (Pro hardening baseline): `Asterisk.Sdk.Pro/docs/decisions/0002-production-hardening-baseline.md`
  - ADR-0025 SDK (Push NATS subscribe + loop prevention): `Asterisk.Sdk/docs/decisions/0025-push-nats-subscribe-and-loop-prevention.md`
  - Roadmaps: Platform `docs/roadmap.md`, Pro `docs/roadmap.md`, SDK `docs/roadmap.md`

---

## Purpose

Decidir **dónde queremos estar** en 12-24 meses con el stack completo (SDK + Pro + Platform + Web), para que todas las decisiones tácticas subsecuentes (mover Pro.Resilience al SDK, extraer VoiceAi, split repo, política commercial/open, cadencia release, narrativa pública) se ordenen desde una visión coherente en lugar de ejecutarse local.

Sin este documento, decisiones individuales se toman con información parcial y producen rework en 3-6 meses cuando la dirección estratégica cambia. Ejemplo concreto: si movemos `Pro.Resilience` → SDK sin decidir si habrá un tier `Asterisk.Framework` intermedio, y 6 meses después creamos ese tier, Resilience se mueve 2 veces (2 breaking changes en vez de 1).

## Key questions this document answers

1. **Tier map definitivo.** ¿3 tiers (SDK/Pro/Platform, status quo con narrativa refinada), 4 tiers (con `Asterisk.Framework` MIT intermedio entre SDK y Pro), o split SDK en 2 repos (protocol-only vs runtime)?
2. **Asignación de cada primitive a un tier** con justificación (Resilience, EventStore, Cluster inbound/primitives, Retention, VoiceAi, SignalR, MultiTenant, Licensing, Routing, Analytics, Session correlation).
3. **Identity + naming per tier** (framework vs SDK vs platform; nombre público vs nombre código; cómo llamarle al producto en README + PackageTags + Product metadata).
4. **Release / distribution model** (releases coordinados vs independientes; meta-packages vs granular; automation level; feed local vs nuget.org; dependency bot).
5. **Pricing / licensing política** (qué queda MIT vs commercial — principios explícitos en vez de caso-único).
6. **SLO por tier** (qué garantiza cada uno — at-most-once, at-least-once, durable, cluster, SaaS).
7. **Competitive positioning** (vs AsterNET como SDK alternative, vs Genesys/Five9/CXone/Amazon Connect como contact-center platform, vs Vapi/Retell/LiveKit Agents como voice AI framework).
8. **Roadmap secuenciado 6 meses** con dependencies explícitas entre decisiones.

---

## §1 — Situación actual (síntesis)

### 1.1 El stack en 1 diagrama

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Asterisk.Platform.Web (React 19, 1.8.0)                                  │
│   — SaaS UI omnichannel contact center                                   │
└──────────────────────────────────────────────────────────────────────────┘
        ▲ HTTP + SignalR
        │
┌──────────────────────────────────────────────────────────────────────────┐
│ Asterisk.Platform (commercial, 30 pkgs, 1.8.1)                          │
│   — API host (59 endpoint groups), Identity, Billing, 11 channels,       │
│     Conversations, Flows, Bot, KB, Surveys, Automation, Audit, Media     │
└──────────────────────────────────────────────────────────────────────────┘
        ▲ PackageReference × 21 Pro (1.8.1-pro) + 2 Sdk directos (1.11.1)
        │
┌──────────────────────────────────────────────────────────────────────────┐
│ Asterisk.Sdk.Pro (commercial, 25 pkgs, 1.8.1-pro)                       │
│   — Enterprise extensions: Dialer, Cluster, EventStore, Analytics,      │
│     CallAnalytics, AgentAssist, Licensing, MultiTenant, Routing,        │
│     Realtime, Push (SignalR hub + bridges), Resilience, Retention       │
└──────────────────────────────────────────────────────────────────────────┘
        ▲ PackageReference × ~14 SDK (1.11.1 pinned internally)
        │
┌──────────────────────────────────────────────────────────────────────────┐
│ Asterisk.Sdk (MIT base, 24 pkgs, 1.13.0)                                │
│   — Telecom wrappers (Ami/Agi/Ari/Config) + Runtime (Live/Sessions/     │
│     Activities/Hosting) + Event Fabric (Push + SSE/Webhooks/NATS)       │
│     + AI Layer (Audio/VoiceAi + 7 STT + 4 TTS + OpenAiRealtime)         │
└──────────────────────────────────────────────────────────────────────────┘
```

### 1.2 Identidad declarada vs identidad real

| Repo | Narrativa oficial (README, PackageTags, Product) | Código real observado |
|---|---|---|
| **SDK** | "The modern .NET SDK for Asterisk PBX. AMI, AGI, ARI, Live API, Sessions, Voice AI" — `PackageTags=sdk` | **Framework/runtime.** 13 `IHostedService`/`BackgroundService`, 2 aggregate roots (AsteriskServer, CallSessionManager), in-memory pub/sub broker (RxPushEventBus + Channel&lt;T&gt;), persistence backends (Redis + Postgres para Sessions), SSE HTTP endpoints, Webhook delivery service con retry, NATS outbound bridge (ahora bidirectional en v1.13). 30% del repo son paquetes VoiceAi. |
| **Pro** | "Enterprise contact center SDK" / "Pro packages extend it — they don't replace it" | **Application framework.** 5 `BackgroundService` pipelines, 10 ActivitySources, 16 meters, runtime resilience layer (1.8.0-pro), retention orchestrator, license guard runtime. Extiende sin duplicar — pero ownea primitivas que no son comerciales (Resilience, Retention base). |
| **Platform** | "API host for omnichannel contact center" | **Full contact-center platform.** 30 packages, 11 channels (WhatsApp/SMS/WebChat/Telegram/Email/Messenger/Instagram/Video/Twitter/RCS + voice), Flows engine, Bot, KnowledgeBase, Automation, Surveys, Billing, Identity/RBAC, Media, Mail microservice, Renderer microservice. Narrativa coherente. |

**Conclusión §1.2:** La crisis de identidad está concentrada en el tier **SDK**. Pro y Platform son coherentes con su narrativa; el SDK vende "SDK" pero envía "framework".

### 1.3 Hallazgos estructurales clave (auditoría previa + re-auditoría v1.13.0)

39 hallazgos específicos verificados post-v1.13.0:

- **Cerrados por v1.12/v1.13 (5):** NATS inbound shipped (`NatsBridgeOptions.Subscribe` + `INatsSubscriber` + loop prevention); JetStream fuera de MIT por ADR-0011; `RemotePushEvent` envelope en SDK.
- **Parciales (5):** `NodeId` existe **solo** en `NatsBridgeOptions` + `RemotePushEvent` (no en `PushEventMetadata` general); `AsteriskSemanticConventions` catalog (54 consts) shipped pero `PushActivitySource`/`PushMetrics` no lo adoptan; benchmarks micro existen pero no Push/NATS throughput ni VoiceAi E2E latency.
- **Intactos (29):** Event Fabric core (EventId, SchemaVersion, SequenceNumber, DedupeKey ausentes); Sessions event-sourcing (snapshot-per-mutation, no append log); Resilience en SDK (open-coded en AmiConnection/AriLoggingHandler/WebhookDeliveryService); Cluster primitives MIT (INodeRegistry/IMembershipProvider/IClusterTransport ausentes); Naming (RxPushEventBus ya no usa Rx pero el prefijo está API-locked; doble `AudioSocketServer` en Ari + VoiceAi; `ISessionHandler` (VoiceAi) vs `CallSession` (Sessions) homónimos); VoiceAi positioning (30% del repo, conventions cementadas en core); Boundary leaks (Sessions.csproj con `InternalsVisibleTo Pro.Cluster`); Hosting god-package; Observability business correlation (spans sin `tenant.id`/`call.id`/`event.id`); Load/latency sin harness; README identity.

### 1.4 Smells de layering

**Smell 1 — SDK evolucionando a micro-platform.** Arrastra `StackExchange.Redis`, `Npgsql`, `Microsoft.AspNetCore.*`, `NATS.Client.*` en un MIT "SDK". Consumidor de `Asterisk.Sdk.Ami` obtiene transitivamente todo esto.

**Smell 2 — Pro ownea primitives no-comerciales.** `Pro.Resilience` (circuit breaker + retry + timeout) es infra genérica; `Pro.Storage.Common.Retention` tiene abstracciones que podrían ser base. Inversion de ownership: SDK retries siguen open-coded, MIT users no pueden usar primitives.

**Smell 3 — Pro conoce vocabulario de Platform.** `Pro.Push.SignalR/Bridges/ConversationStatePushBridge.cs` + `ConversationAssignedEvent` + `ConversationTransferredEvent` — "Conversation" es concepto de Platform, no de Pro. Upward leak.

**Smell 4 — Platform bypass Pro.** `Platform.Core.csproj` referencia `Asterisk.Sdk.Push` directo; `Platform.Api.csproj` referencia `Asterisk.Sdk.Hosting` directo. 2-pronged dependency — OK arquitectónicamente, contradice la narrativa strict-ladder.

### 1.5 Estado de distribución

- **Feed local primary:** 139+ nupkg, 48 unique IDs (SDK + Pro). SDK 1.13.0 packed hoy (24 paquetes nuevos).
- **Feed stale** en `/Asterisk.Platform/local-nuget-feed/`: 171 nupkg congelados en SDK 1.5.x + Pro 1.0.x-pro. Directorio huérfano (`NuGet.Config` no lo consume). Bajo riesgo, pero ruido.
- **Versionado:** SDK 1.13.0 HEAD · Pro 1.8.1-pro (pinea SDK 1.11.1) · Platform 1.8.1 (pinea Pro 1.8.1-pro). Platform al día tras bump hoy.
- **CI coordination manual:** SDK publica via tag a nuget.org. Pro override nuget.config para usar nuget.org. **Platform sin `.github/`** — sin CI. Sin dependency-bump bot cross-repo.
- **Disciplina breaking changes:** degrada descendente — SDK full (PackageValidation + PublicApiAnalyzers + BannedApiAnalyzers + Shipped.txt); Pro parcial (solo PublicApiAnalyzers); Platform cero analyzers.

### 1.6 Decisiones que SDK ya tomó y que restringen el espacio del PSD

Post-v1.13.0 hay ADRs en SDK que limitan ciertas opciones:

- **ADR-0011 SDK** — "Push bus in-memory non-durable". Durability queda en Pro (Pro.EventStore, eventual Pro.Push.JetStream).
- **ADR-0025 SDK** — rechaza explícitamente `[JsonPolymorphic]` en `PushEvent` y rechaza reflection-based subtype round-trip. `RemotePushEvent` es envelope opaco con `OriginalEventType` + `RawPayload`. **Cualquier futuro `EventId`/`SchemaVersion` en SDK debe ir en el envelope, no via polymorphic discriminator.**
- **ADR-0023 SDK** — política `PublicApiAnalyzers` en todos los `src/*` + `PackageValidation` + `BannedApiAnalyzers`. Introducir nuevo paquete cuesta 4-5 archivos extra (csproj + PublicAPI.Shipped.txt + PublicAPI.Unshipped.txt + icon + release-notes).

El PSD debe operar dentro de estas decisiones pre-existentes, no contra ellas.

---

## §2 — Tier map — DECISIÓN: Opción A (3 tiers refinados)

> **Status:** decidido con evidencia. Research competitive + open-core + cadence alinean en la misma dirección.

### 2.1 Decisión

**Mantener 3 tiers. NO introducir tier `Asterisk.Framework` intermedio. NO split SDK en 2 repos (deferred a 2.0.0 si algún día). Rebrandear SDK como "Asterisk Runtime for .NET".**

### 2.2 Evidencia que soporta la decisión

**Open-core 3-tier es canónico (6 de 7 comparables):** GitLab, HashiCorp (Vault/Consul/Terraform/Nomad), MongoDB, Grafana Labs, Redis, Confluent — todos usan `OSS / Self-managed Enterprise / Managed Cloud`. Elastic fue 4-tier antes de consolidar.

**Tier proliferation es anti-pattern documentado.** GitLab colapsó 4 tiers → 3 en enero 2021 porque buyers se confundían. Redis consolidó Stack dentro del OSS en 2025. **Introducir Framework intermedio reproduciría el error que otros ya corrigieron.**

**Ningún comparable tiene tier "framework" MIT entre SDK y Enterprise.** Todos los cluster primitives (consensus/membership/gossip), event identity (Kafka producerId + sequence), resilience primitives viven directamente en OSS core. El problema actual no es "falta tier"; es "primitives mal asignadas dentro de los 3 tiers existentes".

**Migration cost de split (Opción C) prohibitivo post-v1.13.** `AsteriskSemanticConventions.VoiceAi` cementado en core MIT; romperlo requiere semver major + migration guide cross-repo + rename de namespaces. Sin ROI evidente.

### 2.3 Implicaciones

El tier SDK actual absorbe primitives mal categorizadas:
- **Pro.Resilience → SDK (MIT)** — evidencia universal (Polly, Resilience4j, Hystrix, failsafe, Envoy outlier-detection son todos OSS). Mantenerlo en commercial es el exact anti-pattern "primitives trapped in commercial".
- **Pro.Storage.Common.Retention base abstractions → SDK** — `IRetentionPolicy` + `IRetentionTarget` + `RetentionService` base. Ventanas específicas + compliance-cert retention se quedan en Pro.
- **EventStore abstract interfaces → SDK** — `IEventStore`, `IEventLog`, `AppendAsync`. Impl Postgres + tenant-aware projectors + audit trail compliance se quedan en Pro.
- **Cluster primitives básicos → SDK** — `INodeRegistry`, `IMembershipProvider`, `IClusterTransport`, NATS bidirectional (ya shipped v1.13). `FailoverCoordinator` + multi-region replication + tenant-aware routing + Raft quorum se quedan en Pro.

Ver §3 para tabla completa.

### 2.4 VoiceAI (Opción D deferred)

Extracción VoiceAI a repo propio **deferred**. Razones:
- Mercado voice-AI frameworks está dominado Python/Node (LiveKit Agents, Pipecat, Vapi, Retell, Bland). Un .NET-only framework estándalone no captura developers en ese mercado.
- La audiencia defendible son **.NET shops con telefonía** — exactamente el actual embedded in SDK. Extraer rompe el wedge (AOT + zero-GC hot path + ARI integration + no Python runtime).
- `AsteriskSemanticConventions.VoiceAi` cementado en core v1.13 aumenta costo de extracción.
- Si/cuando revenue de Pro + Platform soporte línea de producto separada + managed service, re-evaluar. Por ahora: keep en SDK, narrativa integrada.

---

## §3 — Asignación de primitives a tiers

> **Status:** decidido (asume §2 Opción A).

### 3.1 Criterio canónico

**"Single-tenant, single-cluster, best-effort" rule** (destilada de los 5 gates de open-core comparables):

> **MIT (SDK)** si: el primitive es usable por un deployment single-tenant, single-cluster, best-effort semantics. NO tiene commercial vocabulary ("tenant", "SLA", "license"), NO compliance cert, NO cross-cluster coordination, NO enterprise-identity integration.
>
> **Commercial (Pro)** si: cruza al menos uno de los 5 gates:
> 1. **Multi-tenancy** (namespace/tenant isolation REQUERIDA — no "tenant-aware con param opcional")
> 2. **Scale** (algoritmos que CAMBIAN sobre N nodes — Raft quorum, gossip segments, sharding. No simplemente "better throughput")
> 3. **Integration** (SSO/LDAP/SAML/SIEM enterprise-identity)
> 4. **Compliance/audit** (FIPS/HSM/tamper-evident/PCI/HIPAA cert obligada)
> 5. **Cross-DR/multi-region coordination**

### 3.1.1 Decision flowchart (cuando hay duda)

```
┌─────────────────────────────────────────────┐
│ ¿El feature es un primitive                 │
│ (sin domain logic) o feature específica?    │
└─────────────┬───────────────────────────────┘
              │
        ┌─────┴─────┐
        │           │
    primitive   feature
        │           │
        ▼           ▼
   ¿Cruza algún   ¿Cruza algún
   gate de §3.1?  gate de §3.1?
        │           │
   sí ──┴── no  sí ─┴─ no
   │        │   │      │
   ▼        ▼   ▼      ▼
   Pro    **MIT**  Pro  MIT+review
                        quarterly
```

**Default invertido:** cuando hay duda, MIT gana. Hoy la inercia era Pro-por-default; se revierte explícitamente. Features borderline que hoy están en Pro se revisan quarterly con esta regla.

**Casos borderline resueltos:**

- **"Multi-tenant-aware" con `tenantId` como param opcional** → **MIT**. Solo si la isolation/scoping es GARANTIZADA (no solo pasada como param) califica como gate crossed.
- **"Better performance at scale"** sin cambio de algoritmo → **MIT**. Scale gate requiere algoritmo distinto (quorum, sharding, partitioning).
- **"Useful para enterprise"** sin gate específico → **MIT**. "Nice to have for big customers" no es criterio.
- **"Future commercial feature podría necesitarlo"** → **MIT**. Commercial layer puede decorar/extender sin necesidad de ownership.

### 3.2 Tabla de asignación final

| Primitive | Tier actual | Tier correcto | Gate(s) crosed | Acción |
|---|---|---|---|---|
| `CircuitBreaker` + `RetryPolicy` + `Timeout` | Pro (1.8.0-pro) | **SDK** | ninguno | **Migrar.** Polly, Resilience4j, Hystrix — todos OSS. |
| `IEventLog` abstractions (append + read básico) | ausente | **SDK** | ninguno | **Crear.** Sin expected version, sin subscriptions durables. |
| `IEventStore : IEventLog` (completo — expected version + subscriptions + snapshots + checkpoints) | ausente | **Pro** | scale + compliance | **Crear.** Event sourcing real requiere optimistic concurrency; tenant-aware subscriptions; snapshots para replay O(1). |
| `ISessionEventStore` adapter Postgres (hoy en Pro.EventStore) | Pro | **Pro** | compliance/audit + multi-tenant | Mantener. Ahora implementa `IEventStore` completo. |
| `IRetentionTarget` + `RetentionService` base | Pro (1.8.0-pro) | **SDK** | ninguno | **Migrar base.** |
| Retention windows por dominio + DryRun default | Pro | **Pro** | compliance | Mantener specific policies. |
| `INodeRegistry` + `IMembershipProvider` + `IClusterTransport` | ausente | **SDK** | ninguno | **Crear.** Consul/etcd/Redis cluster primitives son OSS. |
| NATS bridge (pub/sub outbound + inbound) | SDK v1.13 ✅ | **SDK** | — | Shipped. |
| JetStream durable consumer | ausente | **Pro** | scale + durability | ADR-0011 confirmado. |
| `FailoverCoordinator` (Raft-like quorum) | Pro | **Pro** | scale + cross-DR | Mantener. |
| `ICallSessionManager` + CallSession | SDK | **SDK** | ninguno | Mantener. |
| Sessions persistence Redis/Postgres | SDK | **SDK** | ninguno | Mantener. (Single-tenant single-cluster primitive.) |
| Push bus + topic registry | SDK | **SDK** | ninguno | Mantener. |
| Push SSE + Webhooks transports | SDK | **SDK** | ninguno | Mantener. |
| Push SignalR hub (PlatformHub) | Pro | **Pro** | multi-tenancy + integration | Mantener. |
| T27 bridges (Cluster/Conversation/Agent state) | Pro | **Platform** para ConversationBridge; **Pro** para Cluster/Agent | upward leak | Mover ConversationBridge → Platform (smell 3). |
| Multi-tenancy (`ITenantContext` + stores) | Pro | **Pro** | multi-tenancy | Mantener. |
| License guard + ECDSA validation | Pro | **Pro** | definitional | Mantener. |
| Routing (skill-based) | Pro | **Pro** | multi-tenancy | Mantener. `ISkillCatalog` ya es abstract en SDK. |
| Analytics engine (realtime) | Pro | **Pro** | multi-tenancy + SLA | Mantener. |
| CallAnalytics (post-call AI) | Pro | **Pro** | compliance (PCI redaction) + multi-tenancy | Mantener. |
| AgentAssist (LLM orchestration) | Pro | **Pro** | compliance + multi-tenancy | Mantener. |
| VoiceAi (Audio/Stt/Tts/OpenAiRealtime) | SDK | **SDK** | ninguno | Mantener. Extracción deferred. |
| `AsteriskSemanticConventions` | SDK v1.13 ✅ | **SDK** | — | Shipped. Pro.OpenTelemetry adopta (opción U). |
| **CloudEvents v1.0 envelope + domain extensions** (reemplaza "EventId + OriginNodeId + SchemaVersion" dispersos en PushEventMetadata) | ausente | **SDK** | ninguno | **Adoptar CloudEvents v1.0 spec** (CNCF estándar, bindings NATS/HTTP/Kafka documented). Extensions formales para `causationid`/`aggregatetype`/`aggregateid`/`sequencenumber`/`tenantid`/`schemaversion`/`hopcount`/`dedupekey`/`signature`. UUIDv7 via `Guid.CreateVersion7()` nativo .NET 9. PushEventMetadata se refactoriza como adapter a CloudEvent. Respeta ADR-0025 (envelope-based, no polymorphic). |
| EventType namespace convention — **Domain vs Integration split** | ausente | **SDK (convención pública)** | — | `asterisk.domain.*` (SDK internal, breakable en minors) vs `asterisk.integration.*` (cross-boundary, semver strict 6-month deprecation). Pro domain/integration idem. Platform integration idem. |
| **Commands ≠ Events** — bus scope | event bus abusado | **SDK (ADR explícito)** | — | Event bus transporta **solo facts** (past tense). Commands via `ICommandDispatcher` separado. Queries nunca por bus. Evita degradación semántica del modelo. |

### 3.3 Stewardship pledge (de open-core research)

**Compromiso explícito:** "Never move a feature from MIT to Commercial. May move features from Commercial to MIT." Publicar en un ADR stewardship (nuevo, SDK y Pro).

HashiCorp BSL (2023) y Redis SSPL (2024) generaron forks (OpenTofu, Valkey) que capturaron cloud providers. Elastic reversó SSPL en 2024 por developer backlash. **Pledge pre-emptivo es el single most trust-building move**.

---

## §4 — Identity + naming per tier — DECISIÓN

> **Status:** decidido con evidencia competitive.

### 4.1 Decisión

**Drop "SDK" framing. Rebrand narrativa a "Asterisk Runtime for .NET".** Mantener nombre del paquete NuGet (`Asterisk.Sdk.*`) por continuidad SEO + PublicAPI + PackageValidation baseline.

### 4.2 Justificación

**El label "SDK" es commodity framing engañoso.** Comparables técnicos:
- `AsterNET` (SDK legacy .NET) — 159 stars, dormant 2023. Passive wrapper.
- `Sufficit.Asterisk` (SDK modular activo) — thin library, .NET Standard 2.0.
- `asterisk-java` (Java reference) — explícitamente dice "library", honesto con su forma.
- `Twilio .NET SDK`, `Vonage .NET SDK` — REST clients a cloud APIs. Passive.

**Asterisk.Sdk envía algo categóricamente distinto:** 24 paquetes, 13 `BackgroundService`/`IHostedService`, 2 aggregate roots, in-memory broker, persistence drivers, SSE HTTP endpoints, Voice AI pipeline. Esto es **framework/runtime** — matching la descripción interna en CLAUDE.md.

**Naming "SDK" fuerza comparación contra AsterNET (legacy) y Sufficit (library)** — comparación apples-to-oranges donde Asterisk.Sdk parece overweight. Renombrar ubica el producto en **espacio competitivo vacío** (no hay .NET runtime para Asterisk comparable) — frente a `asterisk-java` como feature-parity yardstick.

**Microsoft Agent Framework 1.0 (abril 2026) legitimó "framework" en .NET.** El término tiene vigencia actual.

### 4.3 Layout de identidad final

| Repo | Nombre público | Package ID (sin cambio) | Competencia directa |
|---|---|---|---|
| `Asterisk.Sdk` | **Asterisk Runtime for .NET** | `Asterisk.Sdk.*` (24 pkgs) | asterisk-java (Java), espacio vacío .NET |
| `Asterisk.Sdk.Pro` | **Asterisk Enterprise Runtime** (o "Runtime Pro") | `Asterisk.Sdk.Pro.*` (25 pkgs) | MiRTA PBX, Wazo-as-platform, FusionPBX commercial |
| `Asterisk.Platform` | **Asterisk Contact Center** (open-core CCaaS) | `Asterisk.Platform.*` (30 pkgs) | VICIdial (legacy voice), Chatwoot (digital), Wazo UCaaS, 3CX |
| `Asterisk.Platform.Web` | **Asterisk Contact Center Web** | React/TypeScript | idem (frontend del anterior) |

### 4.4 Cambios concretos — rebrand checklist (10 puntos)

**Package IDs NO cambian** (`Asterisk.Sdk.*` estable — SEO + PublicAPI + PackageValidation baseline). **Nombre repo GitHub NO cambia** (URL estable, breaking sería catastrófico). Solo cambia narrativa pública + metadata.

1. **`Asterisk.Sdk/README.md` línea 3 + hero section:** "The modern .NET SDK for Asterisk PBX..." → "**Asterisk Runtime for .NET** — BackgroundService-native event fabric, ARI/AMI/Realtime, VoiceAI-ready, AOT-friendly."
2. **`Asterisk.Sdk/CLAUDE.md` §Project Overview:** "Asterisk.Sdk is a .NET 10 Native AOT SDK..." → "Asterisk Runtime for .NET — framework en .NET 10 Native AOT para Asterisk PBX...".
3. **`Asterisk.Sdk/Directory.Build.props`:**
   - `<Product>Asterisk.Sdk</Product>` → `<Product>Asterisk Runtime for .NET</Product>`.
   - `<PackageTags>asterisk;ami;agi;ari;voip;pbx;telephony;native-aot;sdk</PackageTags>` → `asterisk;ami;agi;ari;voip;pbx;telephony;native-aot;runtime;framework`.
4. **Cada `src/*/Asterisk.Sdk.*.csproj` (24 files):** `<Description>` individual re-redactada. Patrón: "Asterisk Runtime for .NET — [domain]: ..."
5. **`docs/README-commercial.md` + `docs/README-technical.md`:** actualización de narrativa y tagline.
6. **`Examples/**/README.md` (22 example apps):** descripciones cortas ajustadas al nuevo framing.
7. **NuGet.org descriptions:** automático via `<Description>` en csproj. Re-publish en siguiente minor release (v2.0.0-preview1).
8. **GitHub repo topics:** quitar `sdk`, agregar `framework`, `runtime`, `native-aot-framework`. Ajustar About section.
9. **Documentation site** (si/cuando exista): tagline + landing page.
10. **`Asterisk.Sdk.Pro/Directory.Build.props` + README:** "Pro" sigue como brand. Descripción: "Enterprise Runtime for Asterisk — multi-tenant, cluster, compliance, licensing, enterprise AI."
11. **`Asterisk.Platform/README.md`:** "Asterisk Contact Center — open-core omnichannel platform."
12. **Stewardship pledge:** ADR-0027 (SDK) + ADR Pro referenciándolo. Texto público: "Primitives stay MIT. Forever."

**Ejecución:** single PR cross-repo en Mes 5 (v2.0.0-preview1). Todos los cambios coordinados para evitar narrativa inconsistente durante ventana de transición.

---

## §5 — Release + distribution model — DECISIÓN

> **Status:** decidido post-cadence research.

### 5.1 Cadence decision: commit a "framework with annual majors"

**SDK actual: 22 minors/año extrapolado (11 en 6 meses) = 5-10× typical SDK cadence.** Evidencia:

| Categoría | Cadence típica | SDK actual |
|---|---|---|
| **SDKs (AWS/Azure/Twilio/Stripe)** | 2-4 minor/yr + LTS + major 3-5 yrs | 22 minor/yr — **5-10× excesivo** |
| **Frameworks (ASP.NET Core/EF Core/NestJS)** | 4-12 minor/yr + major anual | 22 minor/yr — **~2× excesivo** |
| **Runtime in active dev (0.x preview)** | Sin expectativa | Encaja, pero usa v1.x stable naming |

**Mismatch documentado:** SDK ships **SDK-grade hygiene** (`PackageValidation` + `PublicApiAnalyzers` + `BannedSymbols` + `CompatibilitySuppressions.xml`) mientras corre a **startup-preview velocity** (30 commits en 2 días v1.12→v1.13). Contradicción interna. 60% de OSS maintainers considera abandonar; 44% cita burnout.

**Decisión:** positioning = "framework in active development". Trade-offs asumidos:
- **v1.x queda declarada como "preview series"** públicamente (no romper la línea hoy; narrativa honesta).
- **v2.0.0 será el primer stable release** — planificado **Q4 2026 (noviembre)**, sincronizado con .NET 11 GA cycle. Absorbe migración breaking + rebrand identity (§4) + reasignación de primitives (§3) + CloudEvents adoption. (Original Q3 septiembre era ajustado para el scope real; Q4 da slack de 2 meses.)
- **Cap post-v2.0: 8-12 minors/yr** (framework range). Minor puede carry breaks + migration guide obligatoria.
- **Annual major** (v2.0 Q4 2026, v3.0 Q4 2027) sincronizado con .NET release cycle (noviembre anual).
- **LTS line** en v2.0 (soporte 12 meses después de v3.0 → hasta Q4 2028).

### 5.2 Distribution model

Baseline actual:
- SDK: tag-triggered publish a nuget.org (publish.yml v1.12+).
- Pro: manual pack + push. CI override nuget.config.
- Platform: **sin CI**, solo Docker.
- Feed local `/media/Data/Source/Verbara/local-nuget-feed/` cross-repo dev.

Decisiones:
- **Platform CI setup** — `.github/workflows/ci.yml` (build + test) en Platform. Sin release workflow (Platform no se distribuye por NuGet, solo Docker image).
- **Pro publish.yml** — replicar pattern del SDK (tag-triggered a nuget.org), eliminando manual step.
- **Dependency bot cross-repo** — Renovate preferred (soporta multi-repo + `PackageVersion` Central Package Management). Config: SDK release → auto-PR a Pro; Pro release → auto-PR a Platform. Manual merge.
- **Feed stale** — borrar `/Asterisk.Platform/local-nuget-feed/` o mover a `docs/archived/feed/` para evitar accidental resolution.
- **Meta-packages deferred** — no incluir en v2.0. Evaluar en v2.1+ si hay demanda.

### 5.3 Release coordination

**Independent cadence con compat matrix.** No coordinated releases.

- SDK v2.x compat matrix publicada en cada release: qué Pro versions + Platform versions son compatibles.
- Pro y Platform pueden skip-ship minors SDK siempre que estén dentro de la compat matrix.
- **LTS líneas** permiten skip-ship hasta 12 meses post-major.

---

## §6 — Pricing / licensing política — DECISIÓN

> **Status:** decidido con evidencia open-core research.

### 6.1 Política canónica (los 5 gates)

Ver §3.1 para la regla canónica. Aplicada como política:

1. **Open (MIT, tier SDK):** protocolo wrappers, runtime infra (hosted services, pub/sub base, resilience primitives, retry/circuit/timeout), observability adapters + `AsteriskSemanticConventions` catalog, filesystem config, single-tenant stores (InMemory + single-cluster Postgres/Redis), single-cluster cluster primitives (`INodeRegistry` / `IMembershipProvider` / `IClusterTransport` + NATS bidirectional), **CloudEvents v1.0 envelope + domain extensions + UUIDv7 generation + `IEventLog` append-only abstractions**, retention base abstractions (`IRetentionTarget`/`RetentionService` base), VoiceAi pipeline primitives, `ICommandDispatcher` (commands separados del bus).

2. **Commercial (Pro):** multi-tenant context + isolation, licensing ECDSA + runtime guard, enterprise SLA features (failover coordinator, cross-region replication, Raft quorum), security compliance (PCI redaction at-rest, audit trail with crypto signing via envelope `signature`, FIPS mode), advanced AI orchestration (CallAnalytics post-call, AgentAssist orchestrated), cluster cross-DR coordination, **JetStream durable consumer**, SignalR hub con tenant scoping, **`IEventStore : IEventLog` completo (expected version + durable subscriptions + snapshots + consumer checkpoints) + transactional outbox + DLQ routing**.

3. **SaaS (Platform tier):** Conversations omnichannel abstraction, 11 channels (WhatsApp/SMS/WebChat/Telegram/Email/Messenger/Instagram/Video/Twitter/RCS + voice), Flows engine, Bot orchestration, KnowledgeBase, Automation, Surveys runner, Billing, Identity/RBAC/SSO, Media (storage + recording), Mail microservice, Renderer microservice.

### 6.2 Borderline cases resueltos

| Primitive | Categoría | Gate | Resolución |
|---|---|---|---|
| **Routing (skill-based)** | Pro | multi-tenancy (routing por tenant) | Queda Pro. `ISkillCatalogBase` abstract ya está en SDK — adecuado. |
| **Analytics engine (realtime)** | Pro | multi-tenancy (metrics per tenant) + SLA | Queda Pro. |
| **EventStore Postgres adapter** | Pro | compliance (retention con audit) + multi-tenant scoping | Queda Pro. Las interfaces abstractas migran a SDK. |
| **CallAnalytics** | Pro | compliance (PCI redaction) + multi-tenant | Queda Pro. |
| **Push.SignalR hub** | Pro | multi-tenancy + integration auth | Queda Pro. |
| **Resilience primitives** | Pro | ninguno | **Migra a SDK.** Anti-pattern documentado. |
| **EventStore interfaces abstractas** | ausente | ninguno | **Crear en SDK.** |
| **Cluster membership primitives** | ausente | ninguno | **Crear en SDK.** |

### 6.3 Stewardship pledge

**Formalizar en ADR-stewardship (SDK + Pro):** "Never move a feature from MIT to Commercial. May move Commercial → MIT. Any exception requires explicit major version + 6-month deprecation notice."

HashiCorp BSL + Redis SSPL generaron forks (OpenTofu, Valkey) que capturaron cloud providers. Pledge pre-emptivo es el single most trust-building move en todo el dataset de open-core research.

---

## §7 — SLO por tier — DECISIÓN

> **Status:** decidido (3 tiers; Framework intermedio descartado en §2).

### 7.1 SLOs declarados

| Tier | Delivery | Durability | Cluster | Isolation | Compliance |
|---|---|---|---|---|---|
| **SDK (Runtime, MIT)** | At-most-once (in-memory) + opt-in at-least-once via EventStore base | In-memory + optional single-cluster Postgres/Redis via Sessions stores | Single-cluster, best-effort NATS fan-out (bidirectional post-v1.13) | Single-tenant | None |
| **Pro (Enterprise Runtime)** | At-least-once con EventStore + idempotency via EventId + JetStream durable consumer (ADR-0011) | Durable Postgres event log + retention w/ audit | Multi-cluster failover coordinator + cross-region replication + quorum (Raft) | Multi-tenant via `ITenantContext` + WHERE-clause isolation + tested (v1.8.1-pro IT) | PCI redaction + audit trail (commit-audit-phase2) |
| **Platform (SaaS)** | Delivered via Pro | Delivered via Pro + Platform.Storage | Delivered via Pro | Delivered via Pro | SOC2 track (aspirational post-v2.0 Platform) |

### 7.2 SLOs públicos (post-v2.0)

Publicar formalmente en docs:
- **SDK 2.0 stable:** "best-effort, in-memory broker, single-node". Documented limits.
- **Pro 2.0:** "at-least-once con Pro.EventStore backing, tenant-isolated, Raft-coordinated failover, PCI-ready redaction".
- **Platform (SaaS):** 99.9% uptime SLA (aspirational tier-1 — requiere infra propia para sustentarlo). P95 < 200ms. Multi-region opcional.

### 7.3 Baseline benchmarks requeridos (opción I del plan)

Para sustentar claims publicados:
- **Push bus throughput + P99 latency** bajo contention (BenchmarkDotNet + NBomber harness).
- **NATS bidirectional throughput + reconnect behavior**.
- **VoiceAi E2E latency** (STT → pipeline → TTS) with provider variations.
- **Sessions store Postgres roundtrip** under load.
- Shipped como benchmark suite en `Tests/Asterisk.Sdk.Benchmarks/` + resultados publicados en release notes.

**Target v2.0:** benchmark suite completo antes de publicar SLOs formales.

---

## §8 — Competitive positioning — DECISIÓN

> **Status:** completo, sustentado por research.

### 8.1 Matriz por tier

| Tier | Competidores directos | Competidores indirectos | Positioning narrative |
|---|---|---|---|
| **SDK (Runtime MIT)** | asterisk-java (Java, feature yardstick) | AsterNET (legacy, 159⭐ dormant), Sufficit (library modular) | **"Asterisk Runtime for .NET — framework con event fabric + VoiceAI + AOT. Único en .NET."** Categoría vacía — no hay competidor directo en .NET. |
| **Pro (Enterprise Runtime)** | MiRTA PBX (Asterisk commercial multi-tenant, feature-equivalent) | Wazo-as-platform, FusionPBX commercial modules | **"MiRTA-class multi-tenant realtime Asterisk, .NET-native, con enterprise resilience + compliance + cluster coordination."** |
| **Platform (CCaaS)** | VICIdial (open-source voice, legacy UX), Chatwoot (digital, no voice), Wazo UCaaS (MSP-lane), 3CX (Windows-heritage) | Genesys Cloud CX ($75-$240/agent), Five9 ($119-$299), NICE CXone ($71-$209), Amazon Connect (AWS-native) | **"Self-hosted o BYOC alternative a Genesys CX2/CX3 a 1/3 del per-agent cost, con Asterisk sovereignty + React UI + MSP/BPO/white-label friendly."** |
| **VoiceAI (embedded en SDK)** | Vapi / Retell / Bland AI (managed, Python/Node) | LiveKit Agents / Pipecat (OSS, Python) | **Wedge:** "Voice AI para .NET telephony stacks — AOT + zero-GC hot path + ARI integration + no Python runtime." No es mass-market voice AI framework — es feature del Runtime. |

### 8.2 Positioning narrative unificada

> **Asterisk stack open-core** for developers and enterprises building telephony + contact center:
>
> - **Asterisk Runtime for .NET** (MIT) — the only production-grade Asterisk framework in .NET. AMI/AGI/ARI + Sessions + Voice AI in a Native AOT package. Feature-parity with asterisk-java, zero competition in .NET.
> - **Asterisk Enterprise Runtime** (Pro commercial) — multi-tenant, cluster, licensing, compliance. MiRTA-class feature set, .NET-native.
> - **Asterisk Contact Center** (Platform open-core CCaaS) — self-hosted Genesys alternative at 1/3 price. 11 channels, Flows, Bot, Billing. MSP/BPO/white-label friendly.
>
> **Stewardship:** primitives stay MIT. Forever.

### 8.3 Estrategia de mercado

- **Tier SDK:** atraer .NET community (blog posts, NuGet discoverability, GitHub stars vs AsterNET). Feature-parity matrix vs asterisk-java publicada como sales tool.
- **Tier Pro:** vender a .NET shops que evalúan MiRTA. Diferenciadores: AOT footprint, .NET stack coherence, source code access (commercial license).
- **Tier Platform:** MSP/BPO/white-label. **NO** competir head-on con Genesys enterprise sales motion — perdido por default. Positioning: "tu propia CCaaS white-label con Asterisk sovereignty". Wazo lane (ha raised $14.4M, slot NOT yet dominated).

### 8.4 Defensible moats

1. **AOT Native-first** — ASP.NET Core + .NET 10 + Native AOT compilación. Ningún competidor telefónico cubre esto. Argument de footprint + cold-start + AWS Lambda viability.
2. **Asterisk sovereignty** — on-prem o BYO-cloud. Vs Genesys/Five9/CXone (vendor lock-in), vs Amazon Connect (AWS lock-in).
3. **Open-core transparency** — código MIT consultable; stewardship pledge publicado; forks posibles. Vs commercial competitors opacos.
4. **Wedge voice AI embebido** — no requiere polyglot deployment (Python/Node ausente). Para regulated/on-prem customers.

---

## §9 — Roadmap secuenciado 6 meses — DECISIÓN

> **Status:** secuencia definitiva post-§2-§8.

### 9.1 Milestones

**Mes 0.5 (última semana abril 2026) — Unblock inmediato:**
- **V — Resolver `RemotePushEvent` collision** (ADR-0005 Pro): type-forward vs herencia vs dual namespace decidido. Desbloquea Pro bump SDK en Mes 2.
- Migration guide v1→v2 `docs/migrations/` creado como skeleton — **crecerá incrementalmente con cada migración Mes 2-5** (no escrito de golpe al final).

**Mes 1 (mayo 2026) — Foundation ADRs (scope reducido a 3 críticos):**

**SDK core ADRs (required):**
- **ADR-0026 SDK:** "Product identity — Runtime for .NET (not SDK)". Checklist 10 puntos de §4.4.
- **ADR-0027 SDK:** "Stewardship pledge — never MIT→Commercial, may Commercial→MIT".
- **ADR-0029 SDK:** "Resilience primitives MIT — migración desde Pro.Resilience".

**Secundarios (pueden diferirse a Mes 2 si Mes 1 corre apretado):**
- ADR-0028 SDK: "Cadence commitment — v1.x preview series, v2.0 stable Q4 2026".
- ADR-0003 Pro: "Tier boundaries — 5 gates canonical + decision flowchart".
- ADR-0004 Pro: "Upward leak resolution — ConversationStateBridge → Platform".
- Platform ADR: "Consumer dual-prong pattern (SDK + Pro direct)".

**Mes 2 (junio 2026) — Event model + Resilience migration + automation:**

**Event model foundation (4 ADRs críticos — del análisis profundo del modelo de eventos):**
- **ADR-0030 SDK:** "**CloudEvents v1.0 adoption + domain extensions** — envelope canónico con `causationid`/`aggregatetype`/`aggregateid`/`sequencenumber`/`tenantid`/`schemaversion`/`hopcount`/`dedupekey`/`signature` como CE extensions formales. UUIDv7 (`Guid.CreateVersion7()`) para `id`. Respeta ADR-0025 (envelope-based)".
- **ADR-0031 SDK:** "Domain vs Integration events — `asterisk.domain.*` vs `asterisk.integration.*` namespace convention + stability guarantees diferenciadas (domain breakable en minors; integration semver strict 6-month deprecation)".
- **ADR-0032 SDK:** "Events ≠ Commands — event bus transporta solo facts; commands via `ICommandDispatcher` separado; queries nunca por bus".
- **ADR-0033 SDK:** "**`IEventLog` (SDK MIT) vs `IEventStore` (Pro) split** — SDK ships append + read básico; Pro ships expected version + durable subscriptions + snapshots + consumer checkpoints".

**Sessions leak cleanup ADR (del análisis del roadmap dependencies):**
- **ADR-0034 SDK:** "ISessionInterceptor — contract público reemplaza `InternalsVisibleTo Pro.Cluster` en Sessions.csproj".

**Code migration (primitives):**
- Ship `Asterisk.Sdk.Resilience` MIT. Pro.Resilience → type-forward + re-export.
- Ship `Asterisk.Sdk.EventLog` abstractions MIT (`IEventLog`, append + read). Pro.EventStore hereda + extiende a `IEventStore` completo.
- Ship `Asterisk.Sdk.Retention` base abstractions MIT (`IRetentionTarget`, `RetentionService` base). Pro retention keeps specific policies + **meter re-emit durante ventana** (`Asterisk.Sdk.Pro.Storage.Common.Retention` + `Asterisk.Sdk.Retention` ambos activos v2.0-v2.1 para no romper dashboards).
- SDK adopciones: AmiConnection + AriLoggingHandler + WebhookDeliveryService usan Resilience primitive (eliminando open-coded retry).
- `PushEventMetadata` → adapter a `CloudEvent` (envelope canónico). RemotePushEvent refactor a CloudEvent con `OriginalEventType` + `data`.

**Automation adelantada:**
- **Renovate cross-repo configurado** (no esperar a Mes 5). SDK release → auto-PR a Pro; Pro release → auto-PR a Platform.
- Pro bump SDK 1.11.1 → 1.13.0 (desbloqueado por V en Mes 0.5). Recibe nuevas primitives.
- **U — Pro.OpenTelemetry adopta `AsteriskSemanticConventions`** (dependiente de Pro bump SDK 1.13, ya posible post-bump). 2h aditivo.

**Mes 3 (julio 2026) — Cluster + observability + naming cleanup:**
- Ship `Asterisk.Sdk.Cluster.Primitives` (`INodeRegistry`, `IMembershipProvider`, `IClusterTransport`).
- `PushActivitySource` + `PushMetrics` adoptan `AsteriskSemanticConventions` (tenant.id, call.id, event.id tags — business correlation).
- **Dual `AudioSocketServer` rename** (en Ari → `AriAudioSocketListener`). **Type-forward window abierto**. (Desacoplado de Pro bump Mes 4 para evitar doble churn.)
- Platform CI setup (`.github/workflows/ci.yml`).
- Pro publish.yml automatizado.

**Mes 4 (agosto 2026) — Sessions interceptor + Pro bump cycle:**
- `Sessions.csproj` `InternalsVisibleTo Pro.Cluster` eliminado. `ISessionInterceptor` público reemplaza (ADR-0034).
- Pro bump 1.9.0-pro (consume nuevas primitives MIT + adopta Sessions interceptor público).
- Platform bump a Pro 1.9.0-pro (nuevo consumer cycle). Platform 1.9.0.
- Migration guide crece: secciones de Resilience + EventLog + Cluster primitives documentadas (trabajo incremental desde Mes 2).

**Mes 5 (septiembre 2026) — v2.0 release preparation:**
- SDK v2.0.0-preview1: rebrand README + Product + PackageTags (checklist 10 puntos §4.4). Preview notice.
- Pro v2.0.0-pro-preview1 compatible.
- **Load/latency benchmark suite completa** (NBomber harness + Push/NATS/VoiceAi E2E + Sessions Postgres roundtrip + `CloudEvent` serialization throughput).
- Migration guide v1.x → v2.0 finalizada (trabajo incremental Mes 2-5 culmina).
- Compat matrix publicada.
- Commands vs Events split materializado (`ICommandDispatcher` shipped).

**Mes 6 (octubre 2026) — v2.0 preview2 + polish:**
- SDK v2.0.0-preview2. Feedback loop.
- Integration tests cross-repo completos (SDK v2 + Pro v2-preview + Platform v2-preview).

**Mes 7 (noviembre 2026) — v2.0 ship (coincidiendo con .NET 11 GA):**
- **SDK v2.0.0 stable.** Primera LTS declarada (soporte 12 meses post-v3.0).
- Pro v2.0.0-pro compatible.
- Platform v2.0 compatible. CI funcional.
- Feed stale en `/Asterisk.Platform/local-nuget-feed/` archivado.
- Public stewardship pledge publicado.
- Release notes con competitive positioning (§8 narrativa).
- CloudEvents spec compliance announced.

### 9.2 v2.0 scope summary

- Rebrand: "Asterisk Runtime for .NET" narrative. Package IDs estables.
- MIT expandido: Resilience + `IEventLog` abstractions + Retention base + Cluster primitives + **CloudEvents v1.0 envelope + UUIDv7 + `ICommandDispatcher`**.
- Pro type-forwards para Resilience; `IEventStore : IEventLog` completo (expected version + durable subscriptions + snapshots + checkpoints + transactional outbox + DLQ routing).
- Domain vs Integration events namespace convention activa.
- Events ≠ Commands bus scope.
- Naming cleanup (AudioSocketServer, Sessions leak via ISessionInterceptor público).
- Stewardship pledge publicado.
- Compat matrix + LTS v2.0 declarada.
- Benchmark suite real.
- Platform CI + automated publish (Pro publish.yml) + Renovate cross-repo.
- **CloudEvents spec compliance** (industry-standard wire format).

---

## §10 — Constraints impuestos por decisiones previas del SDK

> **Status:** completo (captura de §1.6).

El PSD opera dentro de estas decisiones ya tomadas:

1. **ADR-0011:** Push bus in-memory non-durable. JetStream durable consumer es Pro-territory permanente.
2. **ADR-0025:** Anti-polymorphic en PushEvent. Cualquier event identity layer (EventId/SchemaVersion/SequenceNumber) debe ir en envelope metadata, no vía `[JsonPolymorphic]`.
3. **ADR-0023:** `PublicApiAnalyzers` + `PackageValidation` + `BannedApiAnalyzers` en todos los `src/*` de SDK. Introducir nuevo paquete requiere csproj + PublicAPI.Shipped.txt + PublicAPI.Unshipped.txt + icon + release-notes (fricción real).
4. **VoiceAi cementado en v1.13:** `AsteriskSemanticConventions.VoiceAi` en core MIT. Opción D (split VoiceAi) requiere trabajo extra de mover conventions + renombrar namespace.
5. **`RemotePushEvent` collision** SDK 1.13 vs Pro.Push.Backplane — bloquea futuro Pro bump SDK 1.13.

---

## §12 — Event Model Technical Reference (detalle arquitectónico)

> **Status:** decidido post-análisis profundo (ver análisis del modelo de eventos en conversación). Esta sección es la spec canónica para ADRs subordinados (-0030 a -0033).

### 12.1 Decisión de alto nivel

**Envelope canónico = CloudEvents v1.0 + domain extensions.** No EventEnvelope custom.

Razones (destiladas del análisis):
- Industry-standard CNCF. Consumido nativamente por Azure Event Grid, AWS EventBridge, Google Eventarc, Kubernetes Events.
- `.NET library oficial` (`CloudNative.CloudEvents` 2.8+, Apache 2.0, AOT-compatible).
- Transport bindings predefinidos (HTTP, NATS, Kafka, MQTT, WebSockets) — no reinventamos.
- Extensions formales cubren todo lo domain-specific sin hacks.
- Credibilidad positioning ante enterprise buyers.
- Forward-compat con ecosystem CNCF.

### 12.2 Wire contract (CloudEvent + extensions)

| Capa | Atributo | Tipo | Obligatoriedad | Descripción |
|---|---|---|---|---|
| **CE core** | `id` | string (UUIDv7) | required | `Guid.CreateVersion7()` — ordenable por tiempo, .NET nativo, DB-native type. |
| CE core | `source` | string (URI) | required | Quién emitió el evento (ej: `sdk.sessions`, `pro.cluster`, `platform.conversations`). |
| CE core | `type` | string | required | Nombre semántico (ej: `asterisk.domain.call.ended`). Ver §12.3 convenciones. |
| CE core | `specversion` | string | required | Fijo `"1.0"` (CloudEvents spec version). |
| CE core | `time` | string (RFC3339) | required | Momento real del hecho — NO momento de publicación. |
| CE core | `datacontenttype` | string | optional | Default `application/json`. Binary formats (`application/avro`, `application/x-msgpack`) soportados. |
| CE core | `dataschema` | string (URI) | optional | Referencia a schema registry si aplica. |
| CE core | `subject` | string | optional | Sub-entidad del source (ej: `/sessions/call-123` bajo source `/sdk.sessions`). |
| CE core | `data` | any (JSON/binary) | optional | Payload del dominio. Opaco respecto al envelope. |
| **CE standard ext** | `partitionkey` | string | optional | Para routing particionado (NATS subject, Kafka partition). Ej: `call-123`. |
| CE standard ext | `traceparent` | string (W3C) | optional | Auto-populated desde `Activity.Current?.Id`. |
| **Domain ext** | `schemaversion` | int | required | Versión del contrato del payload para ese `type`. Incrementa solo en breaking changes. |
| Domain ext | `causationid` | string (UUIDv7) | optional | `id` del evento que causó éste. Cadena causal. |
| Domain ext | `correlationid` | string | optional | Agrupa múltiples eventos de una operación. |
| Domain ext | `aggregatetype` | string | optional | Entidad de negocio (`CallSession`, `Conversation`, `AgentSession`). |
| Domain ext | `aggregateid` | string | optional | ID de la instancia. |
| Domain ext | `sequencenumber` | long | optional | Secuencia incremental por aggregate. Required para aggregate events (validación runtime en `IEventStore.AppendAsync`). |
| Domain ext | `tenantid` | string | optional | null en MIT single-tenant. Required en Pro multi-tenant. |
| Domain ext | `originnodeid` | string | optional | Nodo emisor. Loop prevention + debugging distribuido. |
| Domain ext | `hopcount` | int | optional | Contador de saltos cross-transport. Default 0; incrementa en bridges. |
| Domain ext | `dedupekey` | string | optional | Dedupe semántico (YAGNI inicial; nullable, no documentado en v2.0 a menos que use-case real emerja). |
| Domain ext | `payloadencoding` | string | optional | `inline` (default) \| `reference-http` \| `reference-s3`. Para events grandes. |
| Domain ext | `signature` | string | optional | HMAC-SHA256 del envelope (Pro multi-tenant trust). |
| Domain ext | `keyid` | string | optional | Identificador de key HMAC (tenant-specific). |

### 12.3 EventType namespace convention

**Fórmula:** `<bounded-context>.<domain-or-integration>.<aggregate>.<event-verb-past>`

| Namespace | Estabilidad | Ejemplos |
|---|---|---|
| `asterisk.domain.*` | SDK internal. **Breakable en minors** con migration notes. | `asterisk.domain.call.started`, `asterisk.domain.call.ended`, `asterisk.domain.agent.state-changed` |
| `asterisk.integration.*` | **Cross-boundary. Semver strict. 6-month deprecation window.** | `asterisk.integration.call.completed`, `asterisk.integration.session.closed` |
| `pro.domain.*` | Pro internal. Breakable en minors. | `pro.domain.dialer.campaign.started`, `pro.domain.callanalytics.analysis.completed` |
| `pro.integration.*` | Pro cross-boundary (a Platform / a external consumers). **Semver strict.** | `pro.integration.cluster.node-joined`, `pro.integration.agent-assist.alert.raised` |
| `platform.integration.*` | Platform → external consumers. **Semver strict. 6-month deprecation.** | `platform.integration.conversation.assigned`, `platform.integration.survey.completed` |

**Reglas:**
- Minúsculas + puntos como separador + hyphens internos.
- Verbo en pasado o cambio observable (`started`, `ended`, `assigned`, `state-changed`).
- **NO** incluir versión en el nombre (`call.ended.v2` → usar `schemaversion=2` en envelope).
- **NO** usar nombres técnicos (`OnCallEndedEvent` → usar `call.ended`).

### 12.4 Interfaces

**SDK (MIT):**

```csharp
public interface IEventLog
{
    Task AppendAsync(CloudEvent envelope, CancellationToken ct);
    IAsyncEnumerable<CloudEvent> ReadStreamAsync(string streamId, long? fromVersion, CancellationToken ct);
    IAsyncEnumerable<CloudEvent> ReadByTypeAsync(string eventType, DateTimeOffset? fromUtc, CancellationToken ct);
}

public interface IEventContext
{
    bool IsReplay { get; }              // NO viaja en wire; inyectado por replay driver
    bool IsRemote { get; }              // NO viaja en wire; inyectado por transport layer
    string TransportSource { get; }     // "local" | "nats" | "webhook" | "replay"
    DateTimeOffset ReceivedAtUtc { get; }
    string? AmbientCausationId { get; } // ID del evento actualmente siendo procesado — publishers lo usan automáticamente
}

public interface ICommandDispatcher
{
    Task<TResult> DispatchAsync<TCommand, TResult>(TCommand command, CancellationToken ct);
    // Commands NO viajan por event bus — esta es la ruta separada.
}
```

**Pro (commercial):**

```csharp
public interface IEventStore : IEventLog
{
    // Optimistic concurrency — previene corruption en concurrent writes
    Task AppendAsync(string streamId, long expectedVersion, CloudEvent[] events, CancellationToken ct);

    // Durable subscriptions con checkpointing
    Task<long> GetCheckpointAsync(string consumerName, CancellationToken ct);
    Task SaveCheckpointAsync(string consumerName, long position, CancellationToken ct);
    Task<ISubscription> SubscribeAsync(long fromPosition, Func<CloudEvent, Task> handler, CancellationToken ct);

    // Snapshots — replay O(1) para aggregates grandes
    Task SaveSnapshotAsync(string streamId, long version, ReadOnlyMemory<byte> data, CancellationToken ct);
    Task<Snapshot?> GetLatestSnapshotAsync(string streamId, CancellationToken ct);

    // DLQ routing (events con hopCount >= maxHopCount o N retries fallidos)
    Task RouteToDlqAsync(CloudEvent envelope, string reason, CancellationToken ct);
}
```

### 12.5 Transport bindings

- **In-memory (`IPushEventBus` interno):** `CloudEvent` .NET record directamente. Sin serialization cost.
- **NATS:** CloudEvents NATS Protocol Binding (structured mode — `application/cloudevents+json`). Subject derivado de `partitionkey` + topic pattern.
- **Webhooks:** CloudEvents HTTP Binding (structured mode default — CE attributes en body JSON). `signature` header para HMAC.
- **SSE:** CloudEvents HTTP Binding (structured mode).
- **EventStore (Pro Postgres):** serialización JSON del CloudEvent completo en columna `envelope_json` (JSONB). Indexes en `streamid`, `sequencenumber`, `partitionkey`, `tenantid`.

### 12.6 Loop prevention + dedupe

**Loop prevention (obligatorio en NATS bidirectional + bridges):**
- Al publicar: `originnodeid = currentNode`, `hopcount = 0`.
- Al recibir desde bridge: si `originnodeid == currentNode` → descartar. Si `hopcount >= maxHopCount` (default 3) → descartar. Si `id` ya visto en TTL cache (1-5 min) → descartar. Si no: incrementar `hopcount`, republicar local.
- TTL cache por `id` en memoria (`ConcurrentDictionary<Guid, DateTimeOffset>` + cleanup).

**Dedupe técnico (consumer-side, Pro):**
```
ProcessedEvents
- consumer_name
- event_id (UUIDv7)
- processed_at_utc
- PK: (consumer_name, event_id)
- TTL: 7 días (configurable)
```

Antes de procesar: consulta `ProcessedEvents`. Si existe → skip. Al terminar: insertar en `ProcessedEvents` dentro de la misma transaction que side effects.

**Consumer idempotency pledge:** todos los consumers externos (webhooks, NATS subscribers, EventStore projectors) DEBEN ser idempotentes por `id`. Framework provee `IIdempotentConsumer` helper con `ProcessOnceAsync(envelope, sideEffect)`.

**Nunca asumir exactly-once.** Asume: at-least-once, possible duplicates, possible reordering.

### 12.7 Events vs Commands vs Queries (ADR-0032)

| Concepto | Semántica | Mechanism | Ejemplo |
|---|---|---|---|
| **Event** | Fact que ya ocurrió (past tense) | `IEventLog` / `IPushEventBus` (fan-out, at-least-once) | `asterisk.domain.call.ended` |
| **Command** | Intent para cambiar estado (imperative) | `ICommandDispatcher` (point-to-point, response expected) | `TransferCallCommand(callId, targetAgentId)` |
| **Query** | Request de información (pull) | Direct method call, no bus | `GetCallStateAsync(callId)` |

**Regla del bus:** "event bus transports facts only". Sin esta regla, el modelo colapsa en 6-12 meses (commands disfrazados como events, fan-out imposible de razonar, reply semantics perdidas).

### 12.8 Payload strategy para events grandes

**Inline (default):** payload en `data` field. Target: < 64KB post-serialization.

**Reference pattern** (para transcripts completos, audio metadata, large aggregates):

```json
{
  "payloadencoding": "reference-http",
  "data": {
    "contentRef": "https://asterisk-events-store/blob/abc123",
    "contentSha256": "sha256-hash-del-contenido",
    "contentSize": 2456789,
    "contentType": "application/json"
  }
}
```

Consumer fetch content ref on-demand. Split permite NATS (1MB limit) sin bloquear events grandes.

### 12.9 Migration from `PushEvent` (v1.x → v2.0)

1. **v1.x → v2.0 adapter:** `PushEventMetadata` se refactoriza como adapter bidireccional a `CloudEvent`. PushEvent existentes siguen funcionando — publisher-side los convierte a CloudEvent antes de ir a transport.
2. **`RemotePushEvent` preserved:** sigue existiendo pero ahora transporta `CloudEvent` con `originaltype` + `data`.
3. **Collision resolution (V):** resuelto en Mes 0.5 — Pro.Push.Backplane.RemotePushEvent → type-forward a Sdk.Push.Events.RemotePushEvent (decisión ADR-0005 Pro).
4. **Period de coexistencia:** v2.0 → v2.2 ambos modelos funcionan. v2.3+ PushEvent legacy marked `[Obsolete]`. v3.0 removed.

### 12.10 Observability correlation mínima

Tags obligatorios en todo span emitido durante procesamiento de events (PushActivitySource, EventLog spans):

- `event.id` (CloudEvent `id`)
- `event.type` (CloudEvent `type`)
- `event.source` (CloudEvent `source`)
- `event.schema_version` (extension `schemaversion`)
- `event.aggregate_type` (extension `aggregatetype`)
- `event.aggregate_id` (extension `aggregateid`)
- `event.sequence_number` (extension `sequencenumber`)
- `event.correlation_id` (extension `correlationid`)
- `event.causation_id` (extension `causationid`)
- `event.origin_node_id` (extension `originnodeid`)
- `tenant.id` (si presente)
- `call.id` / `conversation.id` (según aggregate type)

Sin estos tags, los dashboards de observability son slice-blind. Implementado via `AsteriskSemanticConventions.Event.*` constants (extensión al catalog shipped en v1.13).

---

## §11 — Decisiones operacionales inmediatas (pre-PSD)

> **Status:** completo.

Independientes de §2-§9 y ejecutadas en paralelo para desbloquear el stack:

1. ✅ **W — Pack SDK 1.13.0 al feed local.** Ejecutado 2026-04-19. 24 paquetes SDK 1.13.0 en `/media/Data/Source/Verbara/local-nuget-feed/`.
2. ✅ **D — Platform 1.8.1 bump.** Ejecutado 2026-04-19 (commit `d2e4b05`, pushed). Pro 1.7.2-pro → 1.8.1-pro + wired `AddProResilience`/`AddProLicenseGuard`/`AddProRetention` + 5 retention targets.
3. 🟡 **V — Resolver `RemotePushEvent` collision.** ADR nuevo en Pro decidiendo (a) type-forward, (b) herencia, (c) dual namespace. Bloqueante para Pro bump a SDK 1.13.
4. 🟡 **U — Pro.OpenTelemetry adopta `AsteriskSemanticConventions`.** Post-V (depende de Pro bump SDK 1.13). 2h aditivo zero-risk.

---

## Research en paralelo (lanzado 2026-04-19)

Para completar §2, §7, §8 se lanzaron 3 Explore agents investigando:

1. **Competitive landscape** — Vapi, Retell, LiveKit Agents, Genesys, Five9, NICE CXone, Amazon Connect, AsterNET, FreePBX. Positioning real en GitHub, docs, pricing pages.
2. **Open-core best practices** — GitLab, HashiCorp, Elastic, MongoDB, Redis, Grafana. Tier boundaries, qué se movió entre open y commercial, lecciones aprendidas.
3. **SDK cadence sustainability** — ritmo 1.5.3 → 1.13.0 en 6 meses. ¿Insostenible para un "SDK"? ¿Sostenible para un "framework"? ¿Cómo manejan cadencia players comparables?

Resultados alimentarán §2, §7, §8 en próximas iteraciones.

---

## Próximos pasos

1. Drafting §2 cuando lleguen los 3 reportes de subagentes (aproachando decisión tier map).
2. Drafting §3 como tabla refinada post-§2.
3. User approval checkpoint por sección.
4. ADR stubs subordinados (7 archivos, 3 repos) una vez §2-§7 estables.
5. Commit cross-repo coordinado cuando PSD esté finalizado.

**Timeline objetivo:** PSD finalizado + ADRs stubs en 2 semanas.
