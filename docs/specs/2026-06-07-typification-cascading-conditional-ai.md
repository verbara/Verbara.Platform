# Spec — Typification: cascading, conditional, AI-assisted disposition module

- **Date:** 2026-06-07
- **Author:** Verbara maintainer (Harol A. Reina H.)
- **ADR:** [`0029-typification-cascading-conditional-ai-module`](../decisions/0029-typification-cascading-conditional-ai-module.md)
- **Status:** **P0 SHIPPED 2026-06-07** (Pro #2 / Platform #48 / Web #82; Pro v2.7.5-pro). **P1 SHIPPED 2026-06-08** (Platform #50 / Web #92; Platform v2.11.0 / Web v3.7.0-web). **P2a SHIPPED 2026-06-10** (Pro #3 / Platform #52+#53 / Web #93; Pro v2.8.0-pro / Platform v2.12.0 / Web v3.8.0-web). P2b (AutoApply + entities + Pro voice enrichment) + P3–P4 pending.
- **Repos:** `Verbara.Platform` (domain/API) + `Verbara.Platform.Web` (designer + renderer) + `Verbara.Sdk.Pro` (AI wiring, P2 only)

## 1. Problem

Typification (disposition / wrap-up coding) in Verbara is a **flat single-select list** + free-text notes. There is no way to build the **complex, interlinked (cascading/conditional) typification forms** that healthcare, multi-product support, and outbound operations need — even though the platform already has the flow engine, channel/queue context and a full AI stack to power it. Market research (ADR-0029 Context) confirms cascading + conditional + structured + AI-assisted typification is a standard expectation, threaded through a single shared "reason-for-contact" taxonomy.

## 2. Goals / Non-goals

**Goals**

- A hierarchical (cascading) typification taxonomy with **conditional, structured fields** ("respuestas entrelazadas").
- The **same taxonomy reused end-to-end** (IVR/bot capture → routing → screen-pop → pre-fill → typify → automation → analytics).
- **AI auto-suggest + pre-fill** of node path and fields (agent confirms), reusing the Pro AI stack.
- **External data-dips** inside the form (pre-fill, dynamic options, visibility gating), reusing the `http_request` resilience pattern, with a secure variant for PII.
- Configurable **per queue / campaign / channel / direction** with a tenant default.
- **Clean-break** replacement of the flat `Disposition` model (no back-compat; pre-launch), incl. migration consolidation.
- Native-AOT, `TreatWarningsAsErrors`, raw Npgsql, license-gated (`AdvancedTypification`).

**Non-goals (this spec)**

- Replacing the Pro Dialer's campaign `DispositionCode` (stays in Pro; bridged at wrap-up).
- A general-purpose form-builder for arbitrary platform forms (the renderer is typification-scoped, though built reusably).
- Full multilingual translation of transcripts (language is detected/stored only).

## 3. Decisions (from ADR-0029)

D1 Hybrid architecture · D2 shared taxonomy end-to-end · D3 `LicenseFeature.AdvancedTypification` (independent of Dialer) · D4 clean-break rename + migration consolidation · D5 depth configurable (default 5, max 8) · D6 new `Verbara.Platform.Typification` project.

## 4. Domain model (`Verbara.Platform.Typification`)

All types are `sealed`, `{ get; init; }` records/classes, AOT-friendly, registered in `ApiJsonContext` + `PostgresJsonSerializer` source-gen contexts. Schema definition (nodes/fields/dataDips/aiConfig) persists as JSONB deserialized into these typed records; submissions are normalized.

### 4.1 TypificationSchema (per-tenant, versioned, publishable)

```
TypificationSchema : ITenantScoped
  SchemaId        : EntityId
  TenantId        : TenantId
  Name            : string
  Version         : int            // immutable once published; new edits => new version
  IsPublished     : bool
  MaxDepth        : int            // default 5, validated 1..8 (D5)
  Nodes           : IReadOnlyList<TypificationNode>
  Fields          : IReadOnlyList<TypificationField>
  DataDips        : IReadOnlyList<DataDipDef>     // P3 (empty in P0)
  AiConfig        : TypificationAiConfig          // P2 (disabled in P0)
  CreatedAt, UpdatedAt : DateTimeOffset
```

### 4.2 TypificationNode (the cascade)

```
TypificationNode
  NodeId          : EntityId
  ParentNodeId    : EntityId?      // null = root level
  Label           : string         // localized key or literal (i18n via Web)
  Code            : string         // stable reporting code, unique within schema
  SortOrder       : int
  IsLeaf          : bool           // only leaves are selectable as the final outcome
  ChannelApplicability : ChannelType[]?   // null = all channels
  // Leaf-only outcome semantics (the old flat Disposition/DispositionCode collapse here):
  Leaf            : LeafOutcome?    // non-null iff IsLeaf
```

```
LeafOutcome
  Category            : TypificationCategory   // Success | Failure | FollowUp (+ Retry/SystemResult reserved for dialer bridge)
  TriggerRetry        : bool
  RetryDelayMinutes   : int?
  TriggerCallback     : bool
  DialerCode          : string?    // optional bridge to Pro campaign DispositionCode (outbound)
  IsActive            : bool
```

Depth = length of the parent chain; validated `≤ MaxDepth` on publish. `Code` uniqueness validated on publish.

### 4.3 TypificationField (conditional structured capture)

```
TypificationField
  FieldId         : EntityId
  Key             : string         // stable, unique within schema; used in submission + conditions
  Label           : string
  Type            : FieldType      // Text|Textarea|Number|Date|Boolean|Select|MultiSelect|Phone|Lookup
  Required        : bool
  Options         : IReadOnlyList<FieldOption>?   // for Select/MultiSelect (static); or sourced via OptionsDataDipRef
  Validation      : FieldValidation?              // regex | min/max | maxLength
  AttachToNodeId  : EntityId?      // field shown when this node/branch is in the selected path; null = schema-global
  VisibleWhen     : ConditionExpr? // additional show/hide rule ("respuestas entrelazadas")
  PrefillSource   : PrefillRef?    // Metadata key | AiEntity name | DataDip ref (P2/P3)
  OptionsDataDipRef : string?      // P3: populate Options dynamically from a data-dip
  SortOrder       : int
```

```
ConditionExpr          // P0 = single condition; P4 = AND/OR groups
  RefType : ConditionRef   // Field | NodeSelected
  Ref     : string         // field Key, or node Code
  Op      : ConditionOp    // Eq | Neq | In | Contains | Exists | GreaterThan | LessThan
  Value   : string?        // compared value (CSV for In)
```

`FieldType`, `TypificationCategory`, `ConditionRef`, `ConditionOp`, `PrefillSourceKind` are enums (AOT static dispatch).

### 4.4 DataDipDef (P3 — external lookup; reuses http_request shape)

```
DataDipDef
  DipId           : string         // referenced by fields
  Name            : string
  Method          : string         // GET|POST
  UrlTemplate     : string         // {{var}} templating (metadata, field values)
  Headers         : IReadOnlyDictionary<string,string>
  BodyTemplate    : string?
  TimeoutSeconds  : int
  Secure          : bool           // PII path: no plaintext logging, secret store creds
  ResponseMappings: IReadOnlyList<ResponseMapping>  // jsonPath -> targetFieldKey | optionsForFieldKey
```

Executed by a `TypificationDataDipService` reusing the resilience policy keyed `typification.data-dip` (mirrors `flow.http-request`).

### 4.5 TypificationAiConfig (P2)

```
TypificationAiConfig
  Enabled             : bool
  Mode                : AiMode      // SuggestOnly | AutoApplyAboveThreshold
  ConfidenceThreshold : double
  SentimentGating     : bool        // never auto-pick a Success leaf when sentiment VeryNegative
  EntityFieldMap      : IReadOnlyDictionary<string,string>  // AI entity name -> field Key
```

### 4.6 SchemaBinding (scoping)

```
SchemaBinding : ITenantScoped
  BindingId       : EntityId
  TenantId        : TenantId
  Scope           : BindingScope   // Tenant | Queue | Campaign | Channel | Direction
  ScopeRef        : string?        // queueId | campaignId | ChannelType | "inbound"/"outbound"; null for Tenant
  SchemaId        : EntityId
  SubTreeRootNodeId : EntityId?    // optional: bind only a sub-branch of the schema
  Priority        : int
```

**Resolution (most-specific wins):** Queue+Channel → Queue → Campaign → Channel → Direction → Tenant default. Ties broken by `Priority`. Resolved by `ITypificationResolver.ResolveForConversationAsync(conversation)`.

### 4.7 TypificationSubmission (replaces the disposition part of WrapUpRecord)

```
TypificationSubmission : ITenantScoped
  TenantId        : TenantId
  ConversationId  : EntityId
  AgentId         : EntityId
  SchemaId        : EntityId
  SchemaVersion   : int
  SelectedNodePath: IReadOnlyList<EntityId>   // root..leaf
  LeafNodeId      : EntityId
  FieldValues     : IReadOnlyDictionary<string,string>   // key -> value (typed-validated server-side)
  Notes           : string?
  AiSuggested     : bool
  AiConfidence    : double?
  AiAccepted      : bool?         // did the agent keep the AI suggestion?
  Source          : SubmissionSource   // Manual | AutoAi | Rule
  Duration        : TimeSpan
  CompletedAt     : DateTimeOffset
```

## 5. Storage / migration (clean-break, D4)

- **Drop** legacy `dispositions` + `wrap_up_records` (the flat shape) and the `conversations.wrap_up` JSONB disposition payload if redundant.
- **Create**:
  - `typification_schemas (tenant_id, schema_id, name, version, is_published, max_depth, definition JSONB, created_at, updated_at, PK(tenant_id, schema_id, version))` — `definition` holds nodes/fields/dataDips/aiConfig.
  - `typification_bindings (tenant_id, binding_id, scope, scope_ref, schema_id, subtree_root_node_id, priority, PK(tenant_id, binding_id))` + index `(tenant_id, scope, scope_ref)`.
  - `typification_submissions (tenant_id, conversation_id, agent_id, schema_id, schema_version, selected_node_path JSONB, leaf_node_id, field_values JSONB, notes, ai_suggested, ai_confidence, ai_accepted, source, duration_ms, completed_at, PK(tenant_id, conversation_id))` + indexes `(tenant_id, leaf_node_id, completed_at DESC)` and `(tenant_id, completed_at DESC)`.
- Stores: `Postgres*Store` + `InMemory*Store` for schemas, bindings, submissions (mirror the disposition stores being removed). Raw Npgsql, explicit `NpgsqlDbType` on nullable params, `static Map(NpgsqlDataReader)` row mappers.
- **Migration consolidation** (CONFIRMED 2026-06-07 — full baseline squash): collapse `001..034` into a single fresh `001_Baseline.sql` (pre-launch, no data); verified against a clean DB **and** the Postgres test fixtures, executed *before* P0 domain work lands.

## 6. API (`Verbara.Platform.Api`)

All under `LicenseFeature.AdvancedTypification`; admin routes `AdminOnly` + `RequireOperationalTenant()`.

**Licensing mechanics (who consumes / validates).** The `LicenseFeature` enum is **defined in Pro** (`Verbara.Sdk.Pro.Licensing/LicenseFeature.cs`, `[Flags]`) — add `AdvancedTypification = 1 << 9` + tier-mapping. The `.lic` is **issued** by the verbara-website Worker and **validated** (signature / expiry / `AuthorizedImageDigests`) by Pro's `LicenseValidator` inside the AOT image. **Enforcement is in Platform.Api**: `RequireLicenseFeature(...)` tags the group (`LicenseFeatureMetadata.cs`); `LicenseGateMiddleware` checks `ILicenseStatus.LicensedFeatures.HasFlag(...)` → **HTTP 402** (`trial_url`/`upgrade_url`/`contact_sales_url`) if missing. P0 needs only the flag (Platform already references `Pro.Licensing`); P2 AI additionally requires the existing `CallAnalytics`/`AgentAssist` features **and** the Pro runtime engines.

**Admin / designer**
- `GET/POST/PUT/DELETE /admin/typification/schemas[/{id}]` — full CRUD (note: PUT now exists, unlike the old disposition surface).
- `POST /admin/typification/schemas/{id}/publish` — validates depth/code-uniqueness/leaf-only-selectable, bumps version.
- `GET/POST/PUT/DELETE /admin/typification/bindings[/{id}]`.

**Runtime (agent)**
- `GET /conversations/{id}/typification-form` — resolves the binding for the conversation, returns the published schema (or sub-tree), plus (P2) AI pre-fill suggestion and (P3) any pre-resolved data-dip values.
- `POST /conversations/{id}/typify` — submits `{ selectedNodePath, fieldValues, notes, aiAccepted? }`; **server validates** required/visibility/typed fields against the schema; persists a `TypificationSubmission`; bridges to the Pro dialer if the leaf has a `DialerCode` and the conversation is an outbound campaign attempt (replaces the old `/wrapup` disposition path; callback becomes a conditional field gated by `triggerCallback`).
- (P3) `POST /conversations/{id}/typification-form/data-dip/{dipId}` — execute a field data-dip on demand.

DTOs: `TypificationSchemaDto`, `TypificationNodeDto`, `TypificationFieldDto`, `ConditionExprDto`, `SchemaBindingDto`, `TypifyRequest`, `TypificationFormResponse` — all in `ApiJsonContext`.

## 7. AI orchestration (P2)

On wrap-up open (and, for voice, during the call):
1. `CallAnalyticsEngine` (Pro, in-process via DI) yields `CallSummary` (incl. `DispositionCode`) + entities + sentiment from the transcript; digital channels use `ai_classify`/`ai_generate` over the message thread.
2. A `TypificationAiSuggester` maps `DispositionCode`/classified label → a `SelectedNodePath` (by leaf `Code`/`DialerCode`) and maps extracted entities → `FieldValues` via `AiConfig.EntityFieldMap`.
3. Gating: only suggest/auto-apply when `AiConfidence ≥ ConfidenceThreshold`; `SentimentGating` blocks a Success leaf on VeryNegative sentiment.
4. The form returns the suggestion pre-filled; the agent confirms/edits. Persist `AiSuggested`, `AiConfidence`, `AiAccepted`, `Source = AutoAi`, and a `Tag { Source = AutoAi }`.
5. Real-time (voice): `AgentAssistSession` emits an `AgentSuggestion` whose `Metadata` carries the suggested node path for an in-call hint.

No new reflection; Pro engines already exist — P2 is wiring + the suggester + mapping config.

## 8. Web (`Verbara.Platform.Web`)

- **`src/agent/conversation/` — `<DynamicTypificationForm>`**: replaces the disposition block in `wrap-up-dialog.tsx`. Renders the resolved schema: cascading node selectors (level-by-level, depth-aware), then fields by `Type`, evaluating `AttachToNodeId` + `VisibleWhen` reactively (RHF `watch`); client validation mirrors server; triggers data-dips; shows AI pre-fill with an "AI suggested" badge + accept/edit. The legacy callback date/phone becomes a normal conditional field.
- **`src/admin/typification/` — designer**: tree editor (nodes, drag/sort, leaf outcome attrs), field editor (type/required/options/validation), condition editor (`VisibleWhen`), binding editor (scope → schema), data-dip config (P3), AI config (P2), publish action with validation surfacing. Reuses RHF/Zod + base-ui (`render` prop). *(Visual builder UX — drag-drop canvas — is P4; P0 is a structured form-based editor.)*
- **`src/core/api/hooks/use-typification.ts`**: schema/binding CRUD, `useTypificationForm(conversationId)`, `useTypify()`.
- **i18n** ×3 locales (es-419 baseline, en-US, pt-BR) — CI parity gate.

## 9. Scenarios (acceptance walkthroughs)

- **Healthcare (EPS/clínica):** IVR/WhatsApp captures `Citas → Reprogramar → Ginecología` into `reasonPath` (P1); secure data-dip looks up the patient by document (P3); wrap-up pre-selects that path; leaf "Derivado a urgencias" makes a `triageTier` field visible (`VisibleWhen`); PII fields use the secure data-dip. 
- **Multi-product support:** capture `Producto → Modelo → Componente → Síntoma`; entitlement data-dip gates the `RMA` branch; leaf "RMA-initiated" reveals shipping fields; "Out-of-warranty" reveals a `quotedCost` field; AI pre-selects the path from the transcript (P2).

## 10. Constraints & verification

- **Native AOT** (`dotnet publish -p:PublishTrimmed=true` zero warnings), `TreatWarningsAsErrors`, raw Npgsql, DTOs source-gen.
- **Server-authoritative validation** of required/visibility/typed fields (client mirrors for UX only).
- **Tests:** domain (depth/code-uniqueness/condition eval), store round-trips (incl. legacy-clean migration on a clean DB), endpoint (binding resolution most-specific-wins, license gate, validation rejection), Web (renderer conditional show/hide, AI badge, designer publish). Naming `Method_ShouldExpected_WhenCondition`.
- **License:** routes refuse without `AdvancedTypification`; AI paths additionally require Pro AI features.

## 11. Phasing

P0 (core, manual) → P1 (shared capture) → P2 (AI) → P3 (data-dips) → P4 (analytics + builder polish). See ADR-0029 §Phasing. **P0 ✅ SHIPPED 2026-06-07** (Pro #2 / Platform #48 / Web #82). **P1 ✅ SHIPPED 2026-06-08** (Platform #50 / Web #92 → Platform v2.11.0 / Web v3.7.0-web; see [P1 spec](2026-06-08-typification-p1-shared-capture.md)). **P2a ✅ SHIPPED 2026-06-10** (Pro #3 / Platform #52+#53 / Web #93 → Pro v2.8.0-pro / Platform v2.12.0 / Web v3.8.0-web; first real LLM integration via the new `Verbara.Platform.Llm`; see [P2a spec](2026-06-09-typification-p2-ai-auto-disposition.md)). P2b (AutoApply + entities + Pro voice) + P3–P4 pending; this document is the umbrella spec, with per-phase plans authored via the writing-plans skill.

## 12. Open scope (lock in P0 plan)

1. ~~Migration consolidation extent~~ — **RESOLVED 2026-06-07: full baseline squash** (`001_Baseline.sql`).
2. Pro Dialer `DispositionCode` internal rename (deferred; bridge kept for P0).
3. Designer P0 fidelity — structured form editor (chosen) vs partial drag-drop (P4).
