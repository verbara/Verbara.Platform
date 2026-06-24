## 1. Product decision — grandfather vs. immediate cutoff

- [x] 1.1 Review billing + UX implications of immediate cutoff vs. grace window with product owner
- [x] 1.2 Record the decision in `docs/decisions/0032-platformllm-entitlement-immediate-cutoff.md` referencing this change; option A (immediate cutoff — next request after revocation, no `FeatureGateCache` TTL) is APPROVED

## 2. Phase A — Foundation (grounding, done)

- [x] 2.1 Seam confirmed: enforce at the classify endpoint `ConversationEndpoints.GetTypificationSuggestion`, NOT in `DefaultLlmProviderResolver` (only consumer; billing/quota/metering/audit colocated; resolver gating would risk 402 + wrong layer for audit/metric — ADR-0032)
- [x] 2.2 `IFeatureGateService.IsFeatureEnabled(string tenantId, PlanFeature)` is synchronous, registered singleton, reads the per-request-populated `FeatureGateCache` (no TTL; repopulated by `TenantStatusMiddleware`, evicted on plan change/suspension/dunning) → immediate cutoff
- [x] 2.3 Audit/metric emission point identified: `IAuditService.RecordAsync` + a new counter on `TypificationAiMetrics` (meter `verbara.platform.typification.ai`), both already injected in the handler

## 3. Phase B — Core implementation (focused)

- [ ] 3.1 Add `IFeatureGateService` as a `[FromServices]` param to `GetTypificationSuggestion`; after the AI-enabled/`AiMode.Off` gate and before the platform-managed quota pre-check, when `isPlatformManaged && !featureGate.IsFeatureEnabled(tenantId.Value, PlanFeature.PlatformLlm)` → emit audit + metric + `return Results.Ok(EmptySuggestion)`
- [ ] 3.2 Emit structured audit event `typification.ai.platformllm.entitlement_missing` (category `config`, severity `warning`, actor `system`, target = conversation, metadata `aiSource` = `PlatformManaged`) via the injected `IAuditService`
- [ ] 3.3 Add `Counter<long> PlatformLlmEntitlementMissing` (instrument `platformllm.degrade.entitlement_missing`) + a `[LoggerMessage]` (`LogPlatformLlmEntitlementMissing`) to `TypificationAiMetrics`; increment on the degrade
- [ ] 3.4 Confirm the entitlement block precedes the quota pre-check (line ~290) and classifier call so neither `IQuotaEnforcementService.CheckQuotaAsync` nor `ITypificationCreditMeter.RecordAsync` runs on degrade

## 4. Phase C — Integration + verification (batch)

- [ ] 4.1 Add test `GetSuggestion_ShouldDegradeToEmpty_WhenPlatformManagedAndEntitlementMissing` (seed `TenantPlan.Starter` so `PlatformLlm` is absent + `AiSource.PlatformManaged`) → HTTP 200 + `EmptySuggestion`
- [ ] 4.2 Add test `GetSuggestion_ShouldNotMeterOrCheckQuota_WhenEntitlementMissing` → `creditMeter.DidNotReceive().RecordAsync(...)` and `quota.DidNotReceive().CheckQuotaAsync(...)`
- [ ] 4.3 Add test `GetSuggestion_ShouldClassifyAndMeter_WhenPlatformManagedAndEntitled` (seed `TenantPlan.Enterprise`) → meter `Received(1).RecordAsync(...)` (regression that the new gate does not block entitled tenants)
- [ ] 4.4 Add test `GetSuggestion_ShouldNotApplyEntitlementCheck_WhenByo` → BYO path classifies, never quota-gated nor metered (entitlement gate skipped)
- [ ] 4.5 Run `dotnet test Verbara.Platform.slnx` — all tests green, zero warnings (`TreatWarningsAsErrors`)
- [ ] 4.6 Verify AOT gate: `dotnet publish -r linux-x64 -c Release` on `Verbara.Platform.Api` — no IL2026 / IL3050 / IL207x diagnostics; AOT binary boots clean
- [ ] 4.7 CI green on the PR branch (Api.Tests + AOT gate)
