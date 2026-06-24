## 1. Product decision — grandfather vs. immediate cutoff

- [ ] 1.1 Review billing + UX implications of immediate cutoff vs. grace window with product owner
- [ ] 1.2 Record the decision (ADR or plan note in `docs/decisions/` or `docs/plans/active/`) referencing this change; confirm option A (immediate cutoff via `FeatureGateCache` TTL) is approved

## 2. Phase A — Foundation (batch)

- [ ] 2.1 Confirm `IFeatureGateService` is injectable in `DefaultLlmProviderResolver` (check DI registration in `Verbara.Platform.Llm` `ServiceCollectionExtensions`)
- [ ] 2.2 Confirm `FeatureGateCache` TTL and invalidation path for `PlanFeature.PlatformLlm` (review `FeatureGateService` / `FeatureGateCache` in `Verbara.Platform.Core`)
- [ ] 2.3 Identify audit event emission point in the classify path (`ConversationEndpoints.cs` + `ITypificationAuditService` or equivalent) — no new audit service needed if one exists

## 3. Phase B — Core implementation (focused)

- [ ] 3.1 In `DefaultLlmProviderResolver.ResolveAsync`: add `PlanFeature.PlatformLlm` entitlement check after `AiSource == PlatformManaged` branch guard; return `null` if not entitled
- [ ] 3.2 Emit structured audit event `typification.ai.platformllm.entitlement_missing` (tenantId, aiSource) when the check fails — use existing audit/logging infrastructure, no new reflection
- [ ] 3.3 Increment metric counter `typification.ai.platformllm.degrade.entitlement_missing` on every degraded classify (use `[LoggerMessage]` + metrics if available; AOT-safe)
- [ ] 3.4 Verify that the `IMeteringService.RecordUsageAsync(AiAnalysis)` call in the classify path is downstream of the resolver returning `null` (i.e., metering is skipped on degrade) — adjust call site guard if needed

## 4. Phase C — Integration + verification (batch)

- [ ] 4.1 Add test `ResolveAsync_ShouldReturnNull_WhenPlatformManagedAndEntitlementMissing` — tenant with `AiSource.PlatformManaged`, `PlanFeature.PlatformLlm` disabled → resolver returns `null`
- [ ] 4.2 Add test `ResolveAsync_ShouldReturnProvider_WhenPlatformManagedAndEntitled` — tenant with `AiSource.PlatformManaged`, `PlanFeature.PlatformLlm` enabled → resolver returns provider
- [ ] 4.3 Add test `ClassifyEndpoint_ShouldReturnEmptySuggestion_WhenPlatformManagedEntitlementMissing` — end-to-end classify path degrades cleanly, HTTP 200, empty suggestion, no usage record emitted
- [ ] 4.4 Add test `ClassifyEndpoint_ShouldNotRecordUsage_WhenDegradedDueToEntitlementMissing` — assert `IMeteringService` not called with `AiAnalysis` on degrade
- [ ] 4.5 Run `dotnet test Verbara.Platform.slnx` — all tests green, zero warnings (`TreatWarningsAsErrors`)
- [ ] 4.6 Verify AOT gate: `dotnet publish -r linux-x64 -c Release` on `Verbara.Platform.Api` — no IL2026 / IL3050 / IL207x diagnostics; AOT binary boots clean
- [ ] 4.7 CI green on the PR branch (Api.Tests + AOT gate)
