# Typification P2c.2 — Platform-managed metered LLM — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an entitled tenant switch its Typification AI provider from BYO to a Verbara-operated LLM, metered in tokens (commercialized as AI Credits), gated by a new `PlanFeature`, and capped by a monthly credit allowance enforced through the existing Billing package — AI stays strictly opt-in; BYO is unaffected and never metered.

**Architecture:** Approach A — extend existing seams in place (`Verbara.Platform.Llm` resolver, `Verbara.Platform.Billing` metering/quota, `Verbara.Platform.Typification` classify hook, `Verbara.Platform.Api` admin surface). One migration (010). The platform provider is an OpenAI-compatible provider built from host-bound `PlatformLlmOptions` (Verbara's operator key) — never a per-tenant key.

**Tech Stack:** .NET 10 Native AOT, C# 14, `TreatWarningsAsErrors`, xUnit + NSubstitute + FluentAssertions, raw Npgsql via `Verbara.Sdk.Data.Npgsql` (no Dapper), `[JsonSerializable]` source-gen. Test naming `Method_ShouldExpected_WhenCondition`.

**Spec:** `docs/specs/2026-06-23-typification-p2c2-platform-metered-llm.md`. **Branch:** `feat/typification-p2c2-platform-metered-llm`.

---

## File structure (created / modified)

| File | Responsibility | Phase |
|------|----------------|-------|
| `src/Verbara.Platform.Llm/AiSource.cs` *(create)* | `Byo`/`PlatformManaged` discriminator | A |
| `src/Verbara.Platform.Llm/TenantLlmConfig.cs` *(modify)* | add `AiSource` property | A |
| `src/Verbara.Platform.Llm/PlatformLlmOptions.cs` *(create)* | operator key/model/ratio/enabled | A |
| `src/Verbara.Platform.Llm/ServiceCollectionExtensions.cs` *(modify)* | register `IOptions<PlatformLlmOptions>` | A |
| `src/Verbara.Platform.Billing/UsageUnit.cs` *(modify)* | add `Tokens` member | A |
| `src/Verbara.Platform.Billing/TenantQuota.cs` *(modify)* | add `AiCreditsMonthly` | A |
| `src/Verbara.Platform.Core/PlanFeature.cs` *(modify)* | add `PlatformLlm` | A |
| `src/Verbara.Platform.Storage.Postgres/Migrations/010_platform_llm_credits.sql` *(create)* | `ai_source` + `ai_credits_monthly` columns | A |
| `src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantLlmConfigStore.cs` *(modify)* | persist `ai_source` | A |
| `src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantQuotaStore.cs` *(modify)* | persist `ai_credits_monthly` | A |
| `src/Verbara.Platform.Llm/DefaultLlmProviderResolver.cs` *(modify)* | platform-managed resolution branch | B |
| `src/Verbara.Platform.Billing/DefaultQuotaEnforcementService.cs` *(modify)* | `AiAnalysis` credit limit (token-equiv) | B |
| `src/Verbara.Platform.Typification/Ai/ITypificationCreditMeter.cs` *(create)* + `BillingTypificationCreditMeter.cs` *(create, in Api)* | record token usage + in/out metadata | B |
| `src/Verbara.Platform.Api/Endpoints/TenantLlmConfigEndpoints.cs` *(modify)* + `TenantLlmConfigResponse.cs` *(modify)* | opt-in toggle + entitlement | C |
| `src/Verbara.Platform.Api/Endpoints/AiCreditsEndpoints.cs` *(create)* + DTO | tenant credit-usage read | C |
| `src/Verbara.Platform.Api/Endpoints/ConversationEndpoints.cs` *(modify)* | quota pre-check + credit meter hook | C |
| `src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs` *(modify)* | register new DTOs | C |
| `src/Verbara.Platform.Api/Program.cs` *(modify)* | bind `PlatformLlmOptions`, map endpoints, register meter | C |
| `Verbara.Platform.Web` config page + i18n *(modify)* | radio + usage readout | C |

Tests live in `tests/Verbara.Platform.Api.Tests/` (endpoint + meter + quota + resolver) and the relevant unit projects.

---

## Phase A — Foundation (FCM batch: low-risk scaffolding, can land as one commit per task)

### Task A1: `AiSource` enum + `TenantLlmConfig.AiSource`

**Files:**
- Create: `src/Verbara.Platform.Llm/AiSource.cs`
- Modify: `src/Verbara.Platform.Llm/TenantLlmConfig.cs` (add property after `Enabled`, L75)

- [ ] **Step 1 — Create the enum**

`src/Verbara.Platform.Llm/AiSource.cs`:
```csharp
using System.Text.Json.Serialization;

namespace Verbara.Platform.Llm;

/// <summary>
/// Ownership discriminator for a tenant's Typification LLM provider — distinct
/// from <see cref="ProviderType"/> (the provider *family*). <c>Byo</c> uses the
/// tenant's own encrypted key; <c>PlatformManaged</c> uses Verbara's operator
/// key (host-bound <c>PlatformLlmOptions</c>), metered + billed in AI Credits.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AiSource>))]
public enum AiSource
{
    Byo = 0,
    PlatformManaged = 1,
}
```

- [ ] **Step 2 — Add the property to `TenantLlmConfig`**

In `src/Verbara.Platform.Llm/TenantLlmConfig.cs`, after `public bool Enabled { get; init; }` (L75) add:
```csharp
    /// <summary>BYO (tenant key) vs platform-managed (Verbara operator key). Defaults to <see cref="AiSource.Byo"/>.</summary>
    public AiSource AiSource { get; init; }
```
(Default `Byo` = 0 — preserves every existing call site, which omits it.)

- [ ] **Step 3 — Build to verify it compiles**

Run: `dotnet build src/Verbara.Platform.Llm/Verbara.Platform.Llm.csproj -c Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4 — Commit**

```bash
git add src/Verbara.Platform.Llm/AiSource.cs src/Verbara.Platform.Llm/TenantLlmConfig.cs
git commit -m "feat(llm): add AiSource discriminator to TenantLlmConfig"
```

### Task A2: `PlatformLlmOptions` + registration

**Files:**
- Create: `src/Verbara.Platform.Llm/PlatformLlmOptions.cs`
- Modify: `src/Verbara.Platform.Llm/ServiceCollectionExtensions.cs` (`AddPlatformLlm`, L37)

- [ ] **Step 1 — Create the options type**

`src/Verbara.Platform.Llm/PlatformLlmOptions.cs`:
```csharp
namespace Verbara.Platform.Llm;

/// <summary>
/// Host-bound configuration for Verbara's <b>operator-managed</b> Typification
/// LLM (the provider served when a tenant sets <see cref="AiSource.PlatformManaged"/>).
/// The key lives only here — never per-tenant, never serialized to any DTO.
/// </summary>
public sealed class PlatformLlmOptions
{
    /// <summary>Operator master switch. When false, platform-managed tenants degrade to the empty suggestion.</summary>
    public bool Enabled { get; set; }

    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string Model { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 800;
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Tokens per AI Credit (commercial unit). Default 1000. Credits = Σtokens ÷ this ratio (aggregate, never per-call).</summary>
    public long CreditTokenRatio { get; set; } = 1000;
}
```

- [ ] **Step 2 — Register it in `AddPlatformLlm`**

In `src/Verbara.Platform.Llm/ServiceCollectionExtensions.cs`, change the signature (L37-39) to add a second optional configurator and register the options unconditionally (right after the existing `TryAddSingleton<ILlmProviderResolver, DefaultLlmProviderResolver>()` at L63):
```csharp
public static IServiceCollection AddPlatformLlm(
    this IServiceCollection services,
    Action<LlmProviderOptions>? configure = null,
    Action<PlatformLlmOptions>? configurePlatform = null)
{
    // ... existing body up to and including the resolver TryAddSingleton (L63) ...

    var platformOptions = new PlatformLlmOptions();
    configurePlatform?.Invoke(platformOptions);
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(platformOptions));
```
(Registered ALWAYS so the resolver + quota service can depend on `IOptions<PlatformLlmOptions>`; `Enabled` defaults false.)

- [ ] **Step 3 — Build**

Run: `dotnet build src/Verbara.Platform.Llm/Verbara.Platform.Llm.csproj -c Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4 — Commit**

```bash
git add src/Verbara.Platform.Llm/PlatformLlmOptions.cs src/Verbara.Platform.Llm/ServiceCollectionExtensions.cs
git commit -m "feat(llm): add PlatformLlmOptions (operator-managed LLM config)"
```

### Task A3: `UsageUnit.Tokens` + `TenantQuota.AiCreditsMonthly`

**Files:**
- Modify: `src/Verbara.Platform.Billing/UsageUnit.cs` (append member, L14)
- Modify: `src/Verbara.Platform.Billing/TenantQuota.cs` (add property after `MaxActiveAgents`, L16)

- [ ] **Step 1 — Add `Tokens` to `UsageUnit`**

Append to the enum (after `Hours = 5`):
```csharp
    /// <summary>Raw LLM tokens (technical unit). Commercialized as AI Credits via PlatformLlmOptions.CreditTokenRatio.</summary>
    Tokens = 6,
```

- [ ] **Step 2 — Add `AiCreditsMonthly` to `TenantQuota`**

After `public int? MaxActiveAgents { get; set; }` (L16) add:
```csharp
    /// <summary>Monthly platform-LLM allowance in <b>AI Credits</b> (1 credit = PlatformLlmOptions.CreditTokenRatio tokens). Null = unlimited / pay-as-you-go.</summary>
    public long? AiCreditsMonthly { get; set; }
```
(Mirrors the `long? { get; set; }` shape of `MaxMonthlyMessages`.)

- [ ] **Step 3 — Build**

Run: `dotnet build src/Verbara.Platform.Billing/Verbara.Platform.Billing.csproj -c Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4 — Commit**

```bash
git add src/Verbara.Platform.Billing/UsageUnit.cs src/Verbara.Platform.Billing/TenantQuota.cs
git commit -m "feat(billing): add UsageUnit.Tokens and TenantQuota.AiCreditsMonthly"
```

### Task A4: `PlanFeature.PlatformLlm`

**Files:** Modify `src/Verbara.Platform.Core/PlanFeature.cs` (append member after `IpAllowlist`, L18).

- [ ] **Step 1 — Append the member**

```csharp
    /// <summary>Entitlement to use Verbara's platform-managed Typification LLM (metered in AI Credits).</summary>
    PlatformLlm,
```
(Implicit value 14 — append-only, preserves existing ordinals.)

- [ ] **Step 2 — Build**

Run: `dotnet build src/Verbara.Platform.Core/Verbara.Platform.Core.csproj -c Release`
Expected: Build succeeded.

- [ ] **Step 3 — Commit**

```bash
git add src/Verbara.Platform.Core/PlanFeature.cs
git commit -m "feat(core): add PlanFeature.PlatformLlm entitlement"
```

### Task A5: Migration 010

**Files:** Create `src/Verbara.Platform.Storage.Postgres/Migrations/010_platform_llm_credits.sql` (auto-embedded by the `Migrations\*.sql` glob; no csproj edit).

- [ ] **Step 1 — Write the migration**

```sql
-- =============================================================================
-- Verbara.Platform — Platform-managed LLM + AI credit allowance (010)
-- =============================================================================
-- Additive (baseline squashed in 001_Baseline.sql). Two columns for P2c.2:
--   tenant_llm_config.ai_source  — 'Byo' (default) vs 'PlatformManaged'. Stored
--     as TEXT (enum .ToString() name, mirroring provider_type) — no smallint.
--   tenant_quotas.ai_credits_monthly — monthly AI-Credit allowance (1 credit =
--     PlatformLlmOptions.CreditTokenRatio tokens). NULL = unlimited / pay-go.
-- Idempotent (ADD COLUMN IF NOT EXISTS — Postgres 18).
-- =============================================================================

ALTER TABLE tenant_llm_config
    ADD COLUMN IF NOT EXISTS ai_source TEXT NOT NULL DEFAULT 'Byo';

ALTER TABLE tenant_quotas
    ADD COLUMN IF NOT EXISTS ai_credits_monthly BIGINT;
```

- [ ] **Step 2 — Verify it is embedded + ordered after 009**

Run: `dotnet build src/Verbara.Platform.Storage.Postgres/Verbara.Platform.Storage.Postgres.csproj -c Release`
Then confirm the resource is embedded:
Run: `grep -rl "010_platform_llm_credits" src/Verbara.Platform.Storage.Postgres/obj/ || echo "check glob"`
Expected: Build succeeded; the `.sql` is picked up by the existing `<EmbeddedResource Include="Migrations\*.sql" />` glob and sorts after `009_` by ordinal name.

- [ ] **Step 3 — Commit**

```bash
git add src/Verbara.Platform.Storage.Postgres/Migrations/010_platform_llm_credits.sql
git commit -m "feat(storage): migration 010 — ai_source + ai_credits_monthly"
```

### Task A6: Persist `ai_source` and `ai_credits_monthly` in the Postgres stores

**Files:**
- Modify: `src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantLlmConfigStore.cs`
- Modify: `src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantQuotaStore.cs`
- Test: `tests/Verbara.Platform.Storage.Postgres.Tests/...` are container-backed (excluded from the ratchet); the round-trip is asserted via the InMemory store at the endpoint layer in Phase C. Here, verify build + the existing Postgres-store tests still pass when containers are available.

- [ ] **Step 1 — `PostgresTenantLlmConfigStore`: add `ai_source` to SELECT, UPSERT, Row, ToConfig**

`SelectColumns` (L28-30) — append `ai_source`:
```csharp
private const string SelectColumns =
    "tenant_id, provider_type, model, api_key_encrypted, api_key_last4, " +
    "provider_settings, enabled, ai_source, created_at, updated_at";
```
`Row` class (L131): add `public string ai_source { get; init; } = "Byo";` and in `Map`: `ai_source = r.GetString("ai_source"),`.
`UpsertAsync` SQL (L88-107): add `ai_source` to the INSERT column list + `@AiSource` to VALUES + `ai_source = EXCLUDED.ai_source,` to the `DO UPDATE SET`. Param binding (next to `Enabled`, L116): `p.Add(new NpgsqlParameter("AiSource", config.AiSource.ToString()));` (enum→TEXT name, mirroring `ProviderType`).
`Row.ToConfig` (L156): add `AiSource = Enum.TryParse<AiSource>(ai_source, ignoreCase: true, out var src) ? src : AiSource.Byo,` to the `new TenantLlmConfig { ... }`.

- [ ] **Step 2 — `PostgresTenantQuotaStore`: add `ai_credits_monthly`**

`GetAsync` SELECT (L18-19): append `, ai_credits_monthly`.
`UpsertAsync` (L29-53): add `ai_credits_monthly` to INSERT columns + `@AiCreditsMonthly` to VALUES + `ai_credits_monthly = EXCLUDED.ai_credits_monthly` to `DO UPDATE SET`. Param (mirror `MaxMonthlyMessages`, L44-51):
```csharp
p.Add(new NpgsqlParameter("AiCreditsMonthly", NpgsqlDbType.Bigint)
    { Value = (object?)quota.AiCreditsMonthly ?? DBNull.Value });
```
`QuotaRow` (L64): add `public long? ai_credits_monthly { get; init; }` + in `Map`: `ai_credits_monthly = r.GetInt64OrNull("ai_credits_monthly"),`.
`ToQuota()` (L87): add `AiCreditsMonthly = ai_credits_monthly,`.

- [ ] **Step 3 — Build both projects**

Run: `dotnet build src/Verbara.Platform.Storage.Postgres/Verbara.Platform.Storage.Postgres.csproj -c Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4 — Commit**

```bash
git add src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantLlmConfigStore.cs src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantQuotaStore.cs
git commit -m "feat(storage): persist ai_source + ai_credits_monthly"
```

> **Phase A checkpoint:** `dotnet build Verbara.Platform.slnx -c Release` clean. Review before Phase B.

---

## Phase B — Critical components (FCM: one focused subagent per task — the load-bearing logic)

### Task B1: Platform-managed resolution branch in `DefaultLlmProviderResolver`

**Files:**
- Modify: `src/Verbara.Platform.Llm/DefaultLlmProviderResolver.cs`
- Test: `tests/Verbara.Platform.Api.Tests/Llm/DefaultLlmProviderResolverPlatformTests.cs` *(create)* — (resolver tests live in Api.Tests where DI + options are easy to wire; follow the existing resolver test location if one exists).

The resolver ctor gains `IOptions<PlatformLlmOptions>`; `ResolveAsync` bypasses the BYO key-guard for `PlatformManaged` and requires `PlatformLlmOptions.Enabled`; `BuildWithPolicy` builds an OpenAI-compatible provider from the platform options; `ComputeFingerprint` includes `AiSource` + a platform-options token.

- [ ] **Step 1 — Write failing tests**

`tests/Verbara.Platform.Api.Tests/Llm/DefaultLlmProviderResolverPlatformTests.cs`:
```csharp
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Api.Tests.Llm;

public sealed class DefaultLlmProviderResolverPlatformTests
{
    private static (DefaultLlmProviderResolver resolver, ITenantLlmConfigStore store) Build(PlatformLlmOptions platform)
    {
        var store = Substitute.For<ITenantLlmConfigStore>();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var sp = Substitute.For<IServiceProvider>();
        var resolver = new DefaultLlmProviderResolver(store, httpFactory, sp,
            meterFactory: null, loggerFactory: null, platformOptions: Options.Create(platform));
        return (resolver, store);
    }

    private static TenantLlmConfig PlatformCfg() => new()
    {
        TenantId = EntityId.From("t1"),
        ProviderType = ProviderType.OpenAiCompatible,
        Model = "ignored-when-platform",
        AiSource = AiSource.PlatformManaged,
        Enabled = true,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task ResolveAsync_ShouldReturnProvider_WhenPlatformManagedAndOperatorEnabled()
    {
        var (resolver, store) = Build(new PlatformLlmOptions { Enabled = true, ApiKey = "op-key", Model = "gpt-x", BaseUrl = "https://op" });
        store.GetAsync(Arg.Any<EntityId>(), Arg.Any<CancellationToken>()).Returns(PlatformCfg());

        var resolved = await resolver.ResolveAsync(EntityId.From("t1"), CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ModelId.Should().Be("gpt-x"); // platform model wins, not config.Model
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenPlatformManagedButOperatorDisabled()
    {
        var (resolver, store) = Build(new PlatformLlmOptions { Enabled = false, ApiKey = "op-key", Model = "gpt-x" });
        store.GetAsync(Arg.Any<EntityId>(), Arg.Any<CancellationToken>()).Returns(PlatformCfg());

        var resolved = await resolver.ResolveAsync(EntityId.From("t1"), CancellationToken.None);

        resolved.Should().BeNull(); // fail-closed: operator disabled platform LLM
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotRequireTenantKey_WhenPlatformManaged()
    {
        var (resolver, store) = Build(new PlatformLlmOptions { Enabled = true, ApiKey = "op-key", Model = "gpt-x" });
        store.GetAsync(Arg.Any<EntityId>(), Arg.Any<CancellationToken>()).Returns(PlatformCfg()); // config has NO ApiKey

        var resolved = await resolver.ResolveAsync(EntityId.From("t1"), CancellationToken.None);

        resolved.Should().NotBeNull(); // BYO key-guard is bypassed for PlatformManaged
    }
}
```

- [ ] **Step 2 — Run; verify they fail to compile (ctor param + behavior not present)**

Run: `dotnet test tests/Verbara.Platform.Api.Tests/ --filter "FullyQualifiedName~DefaultLlmProviderResolverPlatformTests" -c Release`
Expected: FAIL — ctor has no `platformOptions` parameter.

- [ ] **Step 3 — Add the ctor param + field**

In `DefaultLlmProviderResolver.cs` add a field `private readonly PlatformLlmOptions _platform;` and extend the ctor (L37) with `IOptions<PlatformLlmOptions>? platformOptions = null` (last param), assigning `_platform = platformOptions?.Value ?? new PlatformLlmOptions();`.

- [ ] **Step 4 — Add the PlatformManaged branch to `ResolveAsync`**

Replace the fail-closed guard (L59, currently `if (config is null || !config.Enabled || string.IsNullOrEmpty(config.ApiKey))`) with source-aware logic:
```csharp
var unusable = config is null
    || !config.Enabled
    || (config.AiSource == AiSource.PlatformManaged
            ? !_platform.Enabled                       // platform: operator switch
            : string.IsNullOrEmpty(config!.ApiKey));   // BYO: tenant key required
if (unusable)
{
    if (_cache.TryRemove(tenantId.Value, out var stale))
        (stale.Resolved.Provider as IDisposable)?.Dispose();
    return null;
}
```

- [ ] **Step 5 — Build the platform provider in `BuildWithPolicy`**

At the top of `BuildWithPolicy` (L107), branch before the existing `effective`/`switch`:
```csharp
if (config.AiSource == AiSource.PlatformManaged)
{
    var platformEffective = new LlmEffectiveOptions(
        BaseUrl:        _platform.BaseUrl,
        ApiKey:         _platform.ApiKey,
        Model:          _platform.Model,
        Temperature:    _platform.Temperature,
        MaxTokens:      _platform.MaxTokens,
        TimeoutSeconds: _platform.TimeoutSeconds);
    return new OpenAiCompatibleLlmProvider(
        _httpFactory.CreateClient(OpenAiClientName),
        platformEffective,
        policy, _meterFactory,
        _loggerFactory?.CreateLogger<OpenAiCompatibleLlmProvider>());
}
```
And in `Build` (L93), the model passed to `ResolvedLlmProvider` must be the platform model when platform-managed:
```csharp
private ResolvedLlmProvider Build(TenantLlmConfig config) =>
    new(BuildWithPolicy(config, _policy),
        config.AiSource == AiSource.PlatformManaged ? _platform.Model : config.Model);
```

- [ ] **Step 6 — Fingerprint includes `AiSource` + platform version**

In `ComputeFingerprint` (L153), make it an instance method (it needs `_platform`) OR pass a platform token. Minimal: change the signature to `private string ComputeFingerprint(TenantLlmConfig config)` (drop `static`) and append two segments to the `string.Join`:
```csharp
    config.AiSource.ToString(),
    config.AiSource == AiSource.PlatformManaged
        ? $"plat:{_platform.Enabled}:{_platform.Model}:{_platform.BaseUrl}:{(_platform.ApiKey is { Length: >= 4 } pk ? pk[^4..] : "none")}"
        : "byo");
```
(So an operator key/model rotation evicts platform-managed entries.)

- [ ] **Step 7 — Run the tests; verify pass**

Run: `dotnet test tests/Verbara.Platform.Api.Tests/ --filter "FullyQualifiedName~DefaultLlmProviderResolverPlatformTests" -c Release`
Expected: PASS (3/3).

- [ ] **Step 8 — Run the existing resolver/BYO tests to confirm no regression**

Run: `dotnet test tests/Verbara.Platform.Api.Tests/ --filter "FullyQualifiedName~LlmProviderResolver" -c Release`
Expected: PASS (BYO behavior unchanged — `AiSource.Byo` default preserves the key-guard).

- [ ] **Step 9 — Commit**

```bash
git add src/Verbara.Platform.Llm/DefaultLlmProviderResolver.cs tests/Verbara.Platform.Api.Tests/Llm/DefaultLlmProviderResolverPlatformTests.cs
git commit -m "feat(llm): platform-managed resolution branch (operator key, fail-closed)"
```

### Task B2: `AiAnalysis` credit limit in `DefaultQuotaEnforcementService`

**Files:**
- Modify: `src/Verbara.Platform.Billing/DefaultQuotaEnforcementService.cs`
- Test: `tests/Verbara.Platform.Api.Tests/Billing/AiCreditQuotaTests.cs` *(create)*

The service learns the credit→token ratio from `IOptions<PlatformLlmOptions>` and maps `UsageType.AiAnalysis` to a token-equivalent limit `AiCreditsMonthly × ratio`. (`PlatformLlmOptions` lives in `Verbara.Platform.Llm`; Billing already has no dep on Llm — add a project reference `Verbara.Platform.Billing → Verbara.Platform.Llm`, which is acyclic since Llm does not reference Billing.)

- [ ] **Step 1 — Write failing tests**

`tests/Verbara.Platform.Api.Tests/Billing/AiCreditQuotaTests.cs`:
```csharp
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Api.Tests.Billing;

public sealed class AiCreditQuotaTests
{
    private static DefaultQuotaEnforcementService Build(long? aiCredits, decimal consumedTokens, QuotaAction action)
    {
        var quotaStore = Substitute.For<ITenantQuotaStore>();
        quotaStore.GetAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = new TenantId("t1"), AiCreditsMonthly = aiCredits, QuotaAction = action });
        var usageStore = Substitute.For<IUsageRecordStore>();
        usageStore.GetSummaryByTypeAsync(Arg.Any<TenantId>(), UsageType.AiAnalysis,
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new UsageSummary { TenantId = new TenantId("t1"), PeriodStart = default, PeriodEnd = default,
                UsageType = UsageType.AiAnalysis, TotalQuantity = consumedTokens, RecordCount = 1, LastUpdatedAt = default });
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero));
        return new DefaultQuotaEnforcementService(quotaStore, usageStore, clock,
            Options.Create(new PlatformLlmOptions { CreditTokenRatio = 1000 }));
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenUnderCreditAllowance()
    {
        var svc = Build(aiCredits: 10, consumedTokens: 5000m, QuotaAction.SoftBlock); // 5 of 10 credits
        var r = await svc.CheckQuotaAsync(new TenantId("t1"), UsageType.AiAnalysis, 1m, CancellationToken.None);
        r.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldSoftBlock_WhenCreditAllowanceExhausted()
    {
        var svc = Build(aiCredits: 10, consumedTokens: 10000m, QuotaAction.SoftBlock); // 10 of 10 credits
        var r = await svc.CheckQuotaAsync(new TenantId("t1"), UsageType.AiAnalysis, 1m, CancellationToken.None);
        r.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllowUnlimited_WhenAiCreditsMonthlyNull()
    {
        var svc = Build(aiCredits: null, consumedTokens: 999999m, QuotaAction.HardBlock);
        var r = await svc.CheckQuotaAsync(new TenantId("t1"), UsageType.AiAnalysis, 1m, CancellationToken.None);
        r.Allowed.Should().BeTrue(); // null = unlimited
    }
}
```

- [ ] **Step 2 — Run; verify fail**

Run: `dotnet test tests/Verbara.Platform.Api.Tests/ --filter "FullyQualifiedName~AiCreditQuotaTests" -c Release`
Expected: FAIL — ctor has no options param; AiAnalysis limit is null (unlimited) so the SoftBlock test fails.

- [ ] **Step 3 — Add the project reference (if absent)**

Run: `dotnet add src/Verbara.Platform.Billing/Verbara.Platform.Billing.csproj reference src/Verbara.Platform.Llm/Verbara.Platform.Llm.csproj`
(Acyclic: Llm has no Billing reference. If it already exists, this is a no-op.)

- [ ] **Step 4 — Extend the service ctor + `GetLimitForType`**

Add `using Verbara.Platform.Llm;` and `using Microsoft.Extensions.Options;`. Ctor (L11) gains `IOptions<PlatformLlmOptions> platformOptions`; store `private readonly long _creditTokenRatio = Math.Max(1, platformOptions.Value.CreditTokenRatio);`. Change `GetLimitForType` (L67) to take the ratio and map AiAnalysis:
```csharp
private long? GetLimitForType(TenantQuota quota, UsageType type) => type switch
{
    UsageType.VoiceInbound or UsageType.VoiceOutbound => quota.MaxMonthlyVoiceMinutes,
    UsageType.SmsInbound or UsageType.SmsOutbound or
    UsageType.WhatsAppInbound or UsageType.WhatsAppOutbound or
    UsageType.EmailInbound or UsageType.EmailOutbound or
    UsageType.TelegramInbound or UsageType.TelegramOutbound => quota.MaxMonthlyMessages,
    UsageType.RecordingStorage or UsageType.MediaStorage => quota.MaxStorageBytes,
    UsageType.AiAnalysis => quota.AiCreditsMonthly is { } c ? c * _creditTokenRatio : null, // credits → token-equiv
    _ => null,
};
```
(Drop `static` since it now reads `_creditTokenRatio`. The `CheckQuotaAsync` flow is otherwise unchanged — it sums `UsageType.AiAnalysis` tokens from `UsageRecord`s and compares against the token-equivalent limit, so SoftBlock/HardBlock/Warn map exactly as for other types.)

- [ ] **Step 5 — Run; verify pass**

Run: `dotnet test tests/Verbara.Platform.Api.Tests/ --filter "FullyQualifiedName~AiCreditQuotaTests" -c Release`
Expected: PASS (3/3).

- [ ] **Step 6 — Commit**

```bash
git add src/Verbara.Platform.Billing/ tests/Verbara.Platform.Api.Tests/Billing/AiCreditQuotaTests.cs
git commit -m "feat(billing): AiAnalysis monthly credit allowance (token-equivalent limit)"
```

### Task B3: `ITypificationCreditMeter` + `BillingTypificationCreditMeter`

**Files:**
- Create: `src/Verbara.Platform.Typification/Ai/ITypificationCreditMeter.cs` (interface only — Typification has no Billing dep)
- Create: `src/Verbara.Platform.Api/Services/BillingTypificationCreditMeter.cs` (impl — the Api composition root references both Billing + Llm)
- Test: `tests/Verbara.Platform.Api.Tests/Services/BillingTypificationCreditMeterTests.cs` *(create)*

The meter records ONE `UsageRecord` (`UsageType.AiAnalysis`, `UsageUnit.Tokens`, quantity = total tokens) with `inputTokens`/`outputTokens`/`model` in `Metadata`, via `IMeteringService.RecordBatchAsync` (the only metering API that carries `Metadata`). It is a no-op when total tokens ≤ 0.

- [ ] **Step 1 — Write the interface**

`src/Verbara.Platform.Typification/Ai/ITypificationCreditMeter.cs`:
```csharp
using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Records platform-managed Typification LLM usage for metering/billing. Called
/// ONLY for <c>AiSource.PlatformManaged</c> classifies (BYO is never metered —
/// the tenant pays its own provider). Tokens are the stored unit; AI Credits are
/// derived by aggregation downstream. No-op for non-positive token counts.
/// </summary>
public interface ITypificationCreditMeter
{
    Task RecordAsync(TenantId tenantId, string conversationId, int promptTokens, int completionTokens, int totalTokens, string model, CancellationToken ct);
}
```

- [ ] **Step 2 — Write the failing impl test**

`tests/Verbara.Platform.Api.Tests/Services/BillingTypificationCreditMeterTests.cs`:
```csharp
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Services;

public sealed class BillingTypificationCreditMeterTests
{
    [Fact]
    public async Task RecordAsync_ShouldRecordTokensWithInOutMetadata_WhenTotalPositive()
    {
        var metering = Substitute.For<IMeteringService>();
        var sut = new BillingTypificationCreditMeter(metering);

        await sut.RecordAsync(new TenantId("t1"), "conv1", promptTokens: 30, completionTokens: 70, totalTokens: 100, "gpt-x", CancellationToken.None);

        await metering.Received(1).RecordBatchAsync(
            Arg.Is<IReadOnlyList<UsageRecord>>(r =>
                r.Count == 1 &&
                r[0].UsageType == UsageType.AiAnalysis &&
                r[0].Unit == UsageUnit.Tokens &&
                r[0].Quantity == 100m &&
                r[0].ReferenceId == "conv1" &&
                r[0].Metadata != null &&
                r[0].Metadata!["inputTokens"] == "30" &&
                r[0].Metadata!["outputTokens"] == "70" &&
                r[0].Metadata!["model"] == "gpt-x"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ShouldDoNothing_WhenTotalTokensNonPositive()
    {
        var metering = Substitute.For<IMeteringService>();
        var sut = new BillingTypificationCreditMeter(metering);

        await sut.RecordAsync(new TenantId("t1"), "conv1", 0, 0, 0, "gpt-x", CancellationToken.None);

        await metering.DidNotReceive().RecordBatchAsync(Arg.Any<IReadOnlyList<UsageRecord>>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3 — Run; verify fail**

Run: `dotnet test tests/Verbara.Platform.Api.Tests/ --filter "FullyQualifiedName~BillingTypificationCreditMeterTests" -c Release`
Expected: FAIL — `BillingTypificationCreditMeter` does not exist.

- [ ] **Step 4 — Write the impl**

`src/Verbara.Platform.Api/Services/BillingTypificationCreditMeter.cs`:
```csharp
using System.Globalization;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Typification.Ai;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// <see cref="ITypificationCreditMeter"/> backed by the Billing <see cref="IMeteringService"/>.
/// Records platform-managed LLM token usage as a single <c>AiAnalysis</c>/<c>Tokens</c>
/// <see cref="UsageRecord"/> carrying input/output token counts + model in metadata
/// (via <c>RecordBatchAsync</c> — the metering API that preserves <c>Metadata</c>).
/// </summary>
internal sealed class BillingTypificationCreditMeter(IMeteringService metering, IClock clock) : ITypificationCreditMeter
{
    private readonly IMeteringService _metering = metering ?? throw new ArgumentNullException(nameof(metering));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public Task RecordAsync(TenantId tenantId, string conversationId, int promptTokens, int completionTokens, int totalTokens, string model, CancellationToken ct)
    {
        if (totalTokens <= 0)
            return Task.CompletedTask;

        var record = new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = tenantId,
            UsageType = UsageType.AiAnalysis,
            Quantity = totalTokens,
            Unit = UsageUnit.Tokens,
            Channel = null,
            ReferenceId = conversationId,
            RecordedAt = _clock.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["inputTokens"] = promptTokens.ToString(CultureInfo.InvariantCulture),
                ["outputTokens"] = completionTokens.ToString(CultureInfo.InvariantCulture),
                ["model"] = model,
            },
        };
        return _metering.RecordBatchAsync(new[] { record }, ct);
    }
}
```
(Update the test ctor to pass an `IClock` substitute — `new BillingTypificationCreditMeter(metering, Substitute.For<IClock>())` — or keep the meter clock-free and stamp `RecordedAt` via the metering service; chosen: meter takes `IClock` for an explicit, testable timestamp. Adjust the Step-2 test ctor calls accordingly.)

- [ ] **Step 5 — Run; verify pass**

Run: `dotnet test tests/Verbara.Platform.Api.Tests/ --filter "FullyQualifiedName~BillingTypificationCreditMeterTests" -c Release`
Expected: PASS (2/2).

- [ ] **Step 6 — Commit**

```bash
git add src/Verbara.Platform.Typification/Ai/ITypificationCreditMeter.cs src/Verbara.Platform.Api/Services/BillingTypificationCreditMeter.cs tests/Verbara.Platform.Api.Tests/Services/BillingTypificationCreditMeterTests.cs
git commit -m "feat(typification): credit meter — record platform LLM tokens + in/out metadata"
```

> **Phase B checkpoint:** the three critical units are independently tested. Review before Phase C wires them into endpoints.

---

## Phase C — Integration (FCM batch: wiring, endpoints, surface)

### Task C1: Opt-in toggle + entitlement in `TenantLlmConfigEndpoints`

**Files:**
- Modify: `src/Verbara.Platform.Api/Endpoints/TenantLlmConfigResponse.cs` (add `aiSource` + `platformLlmAvailable` to GET response; add `aiSource` to upsert request)
- Modify: `src/Verbara.Platform.Api/Endpoints/TenantLlmConfigEndpoints.cs` (GET maps the new fields; PUT gates `PlatformManaged` on `PlanFeature.PlatformLlm`)
- Modify: `src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs` (no new types — the existing DTOs gain fields)
- Test: `tests/Verbara.Platform.Api.Tests/Endpoints/TenantLlmConfigPlatformOptInTests.cs` *(create — use the existing endpoint test harness/`WebApplicationFactory` pattern already used for the BYO llm-config endpoints)*

- [ ] **Step 1 — Extend the DTOs**

In `TenantLlmConfigResponse.cs`, add to the `TenantLlmConfigResponse` record (after `Enabled`): `AiSource AiSource,` and `bool PlatformLlmAvailable,` (keep positional order; update `FromConfig` to pass `AiSource: config.AiSource` and accept `platformLlmAvailable` as a param). Add to `UpsertLlmConfigRequest` (after `Enabled`): `AiSource AiSource = AiSource.Byo` (defaulted so existing BYO callers are source-compatible). Add `using Verbara.Platform.Llm;` (already present).

- [ ] **Step 2 — Write the failing test**

`tests/Verbara.Platform.Api.Tests/Endpoints/TenantLlmConfigPlatformOptInTests.cs` (sketch — mirror the existing BYO llm-config endpoint test factory; assert: PUT `aiSource=PlatformManaged` returns 403 when the tenant lacks `PlanFeature.PlatformLlm`, and 200 when the `FeatureGateCache` grants it; GET echoes `aiSource` + `platformLlmAvailable`):
```csharp
[Fact]
public async Task Put_ShouldReject_WhenPlatformManagedWithoutEntitlement() { /* arrange tenant w/o PlatformLlm feature; PUT aiSource=PlatformManaged; assert 403 */ }

[Fact]
public async Task Put_ShouldAccept_WhenPlatformManagedWithEntitlement() { /* grant FeatureGateCache PlatformLlm; PUT; assert 200 + store has AiSource.PlatformManaged */ }

[Fact]
public async Task Get_ShouldEchoAiSourceAndAvailability() { /* assert response.AiSource + PlatformLlmAvailable reflect cache */ }
```
(Use the same `data-*`-free API-level assertions and the existing test fixture that seeds `FeatureGateCache` via `ResolvedFeatures`.)

- [ ] **Step 3 — Run; verify fail**

Run: `dotnet test tests/Verbara.Platform.Api.Tests/ --filter "FullyQualifiedName~TenantLlmConfigPlatformOptInTests" -c Release`
Expected: FAIL.

- [ ] **Step 4 — Gate the PUT + map the GET**

In `UpsertConfig` (L65), inject `[FromServices] IFeatureGateService featureGate`. After the existing `EnsureConfigurePermissionAsync` check and before building the config, add:
```csharp
if (body.AiSource == AiSource.PlatformManaged &&
    !featureGate.IsFeatureEnabled(tenantId.Value, PlanFeature.PlatformLlm))
{
    return Results.Json(
        new ErrorResponse("Platform-managed AI is not included in this tenant's plan."),
        ApiJsonContext.Default.ErrorResponse, statusCode: 403);
}
```
Set `AiSource = body.AiSource` on the `new TenantLlmConfig { ... }`. When `PlatformManaged`, do NOT require/rotate a BYO key (the existing key-preservation block already tolerates an absent key).
In `GetConfig` (L44), inject `[FromServices] IFeatureGateService featureGate`; compute `var available = featureGate.IsFeatureEnabled(tenantId.Value, PlanFeature.PlatformLlm);` and pass it: `TenantLlmConfigResponse.FromConfig(config, platformLlmAvailable: available)` (and for the empty case keep `EmptyLlmConfigResponse`, but include availability — extend `EmptyLlmConfigResponse(bool Configured, bool PlatformLlmAvailable)` and register nothing new since it's an existing serializable type with an added member).

- [ ] **Step 5 — Run; verify pass**

Run: `dotnet test tests/Verbara.Platform.Api.Tests/ --filter "FullyQualifiedName~TenantLlmConfigPlatformOptInTests" -c Release`
Expected: PASS.

- [ ] **Step 6 — Commit**

```bash
git add src/Verbara.Platform.Api/Endpoints/TenantLlmConfig*.cs tests/Verbara.Platform.Api.Tests/Endpoints/TenantLlmConfigPlatformOptInTests.cs
git commit -m "feat(api): platform-managed opt-in toggle gated by PlanFeature.PlatformLlm"
```

### Task C2: `GET /admin/ai/credits` usage endpoint

**Files:**
- Create: `src/Verbara.Platform.Api/Endpoints/AiCreditsEndpoints.cs` + `AiCreditsResponse` DTO
- Modify: `src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs` (register `AiCreditsResponse`)
- Modify: `src/Verbara.Platform.Api/Program.cs` (map the endpoint)
- Test: `tests/Verbara.Platform.Api.Tests/Endpoints/AiCreditsEndpointTests.cs` *(create)*

The endpoint reads the tenant quota + current-period `AiAnalysis` token summary, converts to credits via `PlatformLlmOptions.CreditTokenRatio`, and returns allowance/consumed/remaining/percent.

- [ ] **Step 1 — DTO**

In a new `AiCreditsEndpoints.cs` (namespace `Verbara.Platform.Api.Endpoints`):
```csharp
public sealed record AiCreditsResponse(
    long? AllowanceCredits,
    long ConsumedCredits,
    long? RemainingCredits,
    double UsagePercent,
    DateTimeOffset PeriodEnd,
    QuotaAction ActionOnExhaustion);
```

- [ ] **Step 2 — Failing test** (`AiCreditsEndpointTests.cs`): seed quota `AiCreditsMonthly=10`, usage summary `TotalQuantity=5000` tokens, ratio 1000 → expect `ConsumedCredits=5`, `RemainingCredits=5`, `UsagePercent=50`. Assert 200 + body. (Mirror the BYO llm-config endpoint test fixture.)

- [ ] **Step 3 — Run; verify fail.** `dotnet test ... --filter "FullyQualifiedName~AiCreditsEndpointTests" -c Release` → FAIL.

- [ ] **Step 4 — Handler** (RBAC `typification:ai:configure`, `AdminOnly`, `RequireOperationalTenant`):
```csharp
public static void MapAiCreditsEndpoints(this IEndpointRouteBuilder app)
{
    app.MapGet("/admin/ai/credits", GetCredits)
        .RequireAuthorization("AdminOnly")
        .RequireOperationalTenant();
}

private static async Task<IResult> GetCredits(
    HttpContext context,
    [FromServices] ITenantQuotaStore quotaStore,
    [FromServices] IUsageRecordStore usageStore,
    [FromServices] PermissionResolver permissionResolver,
    [FromServices] IOptions<PlatformLlmOptions> platform,
    [FromServices] IClock clock,
    CancellationToken ct)
{
    var tenantId = GetTenantId(context); // TenantId
    var perms = await ResolveCallerPermissions(context, permissionResolver, tenantId, ct);
    if (!PermissionResolver.HasPermission(perms, "typification:ai:configure"))
        return Results.Forbid();

    var ratio = Math.Max(1, platform.Value.CreditTokenRatio);
    var now = clock.UtcNow;
    var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
    var end = start.AddMonths(1);
    var summary = await usageStore.GetSummaryByTypeAsync(tenantId, UsageType.AiAnalysis, start, end, ct);
    var consumedTokens = summary?.TotalQuantity ?? 0m;
    var consumedCredits = (long)(consumedTokens / ratio);

    var quota = await quotaStore.GetAsync(tenantId, ct);
    long? allowance = quota?.AiCreditsMonthly;
    long? remaining = allowance is { } a ? Math.Max(0, a - consumedCredits) : null;
    double percent = allowance is { } a2 && a2 > 0 ? (double)consumedCredits / a2 * 100 : 0;

    return Results.Ok(new AiCreditsResponse(
        AllowanceCredits: allowance,
        ConsumedCredits: consumedCredits,
        RemainingCredits: remaining,
        UsagePercent: percent,
        PeriodEnd: end,
        ActionOnExhaustion: quota?.QuotaAction ?? QuotaAction.Warn));
}
```
(`GetTenantId` + `ResolveCallerPermissions` mirror the `TypificationEndpoints` helpers — copy the same private helpers into this file, or factor a shared helper; keep consistent with the existing pattern.)

- [ ] **Step 5 — Register the DTO** in `ApiJsonContext.cs` (next to the llm-config block, L63):
```csharp
[JsonSerializable(typeof(AiCreditsResponse))]
```

- [ ] **Step 6 — Map** in `Program.cs` near `MapTenantLlmConfigEndpoints()`: `app.MapAiCreditsEndpoints();`

- [ ] **Step 7 — Run; verify pass.** `dotnet test ... --filter "FullyQualifiedName~AiCreditsEndpointTests" -c Release` → PASS.

- [ ] **Step 8 — Commit**

```bash
git add src/Verbara.Platform.Api/Endpoints/AiCreditsEndpoints.cs src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs src/Verbara.Platform.Api/Program.cs tests/Verbara.Platform.Api.Tests/Endpoints/AiCreditsEndpointTests.cs
git commit -m "feat(api): GET /admin/ai/credits tenant usage endpoint"
```

### Task C3: Wire quota pre-check + credit meter into the classify path

**Files:**
- Modify: `src/Verbara.Platform.Api/Endpoints/ConversationEndpoints.cs` (`GetTypificationSuggestion`, L227)
- Test: `tests/Verbara.Platform.Api.Tests/Endpoints/TypificationSuggestionMeteringTests.cs` *(create — extend the existing suggestion endpoint tests)*

Inject `IQuotaEnforcementService`, `ITypificationCreditMeter`, `ITenantLlmConfigStore` (to read `AiSource`) into the handler. **Only when the resolved config is `PlatformManaged`:** (a) pre-check quota before `ClassifyAsync` (SoftBlock→empty, HardBlock→402), (b) record credits after a successful classify.

- [ ] **Step 1 — Failing test:** platform-managed tenant over its `AiCreditsMonthly` (SoftBlock) → suggestion endpoint returns the empty payload AND `ITypificationCreditMeter.RecordAsync` is NOT called; under allowance → classify proceeds and `RecordAsync` IS called once with the classification's token counts. A BYO tenant → meter NOT called (BYO never metered). (Mirror the existing `GetTypificationSuggestion` test fixture; substitute the quota service + meter.)

- [ ] **Step 2 — Run; verify fail.** → FAIL (handler has no quota/meter params).

- [ ] **Step 3 — Add params + the platform-managed branch.** Add to the handler signature (L227): `[FromServices] IQuotaEnforcementService quota,` `[FromServices] ITypificationCreditMeter creditMeter,` `[FromServices] ITenantLlmConfigStore llmConfigStore,`. After resolving `resolved` and before the existing budget block (L279), read the source once:
```csharp
var llmConfig = await llmConfigStore.GetAsync(EntityId.From(tenantId.Value), ct);
var isPlatformManaged = llmConfig is { AiSource: AiSource.PlatformManaged };

if (isPlatformManaged)
{
    var q = await quota.CheckQuotaAsync(tenantId, UsageType.AiAnalysis, additionalQuantity: 1m, ct);
    if (!q.Allowed)
    {
        // SoftBlock + HardBlock both set Allowed=false; map HardBlock to 402, SoftBlock to the empty degrade.
        var qq = await quota.GetQuotaStatusAsync(tenantId, ct);
        if (qq.Quota?.QuotaAction == QuotaAction.HardBlock)
            return Results.Json(new ErrorResponse("AI credit allowance exhausted."), ApiJsonContext.Default.ErrorResponse, statusCode: 402);
        return Results.Ok(EmptySuggestion); // SoftBlock degrade (AI opt-in floor)
    }
}
```
After the post-success budget recording (L318), add the credit meter call (platform-managed only):
```csharp
if (isPlatformManaged && classification.Usage is { } usage)
{
    await creditMeter.RecordAsync(
        tenantId, conversationId.Value,
        usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens,
        classification.ModelId, ct);
}
```
(`classification` is non-null here — the `classification is null` early-return at L310-311 already ran. `EmptySuggestion`, `tenantId`, `conversationId` are in scope per the harvest.)

- [ ] **Step 4 — Run; verify pass.** `dotnet test ... --filter "FullyQualifiedName~TypificationSuggestionMeteringTests" -c Release` → PASS.

- [ ] **Step 5 — Commit**

```bash
git add src/Verbara.Platform.Api/Endpoints/ConversationEndpoints.cs tests/Verbara.Platform.Api.Tests/Endpoints/TypificationSuggestionMeteringTests.cs
git commit -m "feat(api): meter + quota-gate platform-managed typification classifies"
```

### Task C4: DI wiring in `Program.cs`

**Files:** Modify `src/Verbara.Platform.Api/Program.cs`.

- [ ] **Step 1 — Bind `PlatformLlmOptions` + register the meter.** At the `AddPlatformLlm(...)` call site, pass the platform configurator (bound from configuration section `Llm:Platform`):
```csharp
builder.Services.AddPlatformLlm(
    configure: o => builder.Configuration.GetSection("Llm").Bind(o),
    configurePlatform: p => builder.Configuration.GetSection("Llm:Platform").Bind(p));
```
Register the meter (singleton, like the other Api services): `builder.Services.AddSingleton<ITypificationCreditMeter, BillingTypificationCreditMeter>();`

- [ ] **Step 2 — Build the Api host.** `dotnet build src/Verbara.Platform.Api/Verbara.Platform.Api.csproj -c Release` → succeeded, 0 warnings.

- [ ] **Step 3 — Commit**

```bash
git add src/Verbara.Platform.Api/Program.cs
git commit -m "chore(api): wire PlatformLlmOptions + credit meter into the composition root"
```

### Task C5: Web — opt-in radio + credit usage readout

**Files (Verbara.Platform.Web):**
- Modify the LLM-config admin page component (the P2c.1 config form) — add a "Use Verbara-managed AI (credits)" radio (BYO vs PlatformManaged); disable the BYO key fields when PlatformManaged; show `platformLlmAvailable` (disable the platform option + show an upgrade hint when false).
- Add a credit-usage readout calling `GET /admin/ai/credits` (allowance / consumed / remaining / %).
- Add i18n keys to **EN-US, ES-419, PT-BR** (CI parity enforced).
- Update the typed API hook (`use-typification-llm`) for the `aiSource` field + a new `useAiCredits` query.

- [ ] **Step 1 — Failing vitest** for the hook + the radio behavior (PlatformManaged hides the key field; disabled when not available). `npx vitest run src/.../use-typification-llm.test.ts`.
- [ ] **Step 2 — Implement** the radio + readout + i18n + hook.
- [ ] **Step 3 — Verify:** `npm run build` (type-check), `npx vitest run`, `npm run test:i18n` (locale parity), `npx eslint .` — all green.
- [ ] **Step 4 — Commit** (separate Web PR): `feat(web): platform-managed AI opt-in + credit usage readout`.

> Web ships as its own PR (cross-repo), after the Platform API PR merges and the SDK/Pro feed is unchanged.

---

## Final verification (before PR)

- [ ] `dotnet build Verbara.Platform.slnx -c Release` — 0 warnings (TreatWarningsAsErrors).
- [ ] `dotnet test tests/Verbara.Platform.Api.Tests/ -c Release` — all green (incl. the new resolver/quota/meter/endpoint tests).
- [ ] AOT publish gate: `dotnet publish src/Verbara.Platform.Api/Verbara.Platform.Api.csproj -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:InvariantGlobalization=true -o /tmp/aot-p2c2` → native ELF, 0 `IL2026/IL3050/IL207x`, 0 managed Verbara DLLs (every new DTO is in `ApiJsonContext`).
- [ ] Migration 010 applies idempotently against a fresh + an existing DB (Storage.Postgres container tests, when available).

---

## Self-review (against the spec)

**Spec coverage:**
- §3.1 provider resolution → Tasks A1, A2, B1. ✅
- §3.2 gating + opt-in → Tasks A4, C1. ✅
- §3.3 metering + credit conversion (tokens stored, credits aggregated) → Tasks A3, B3, C3. ✅
- §3.4 quota/allowance → Tasks A3, A6, B2, C3. ✅
- §3.5 admin/usage surface + Web → Tasks C1, C2, C5. ✅
- §4 migration 010 → Task A5, A6. ✅
- §5 request flow → Task C3 (quota pre-check before classify; meter after success). ✅
- §6 fail-closed (opt-in floor, operator key never serialized, BYO never metered) → B1 (degrade), B3/C3 (BYO skip), DTOs never expose the operator key. ✅
- §7 testing → each task is TDD with explicit asserts. ✅
- §9 no Pro release → confirmed (`PlanFeature` is Core; `TypificationAi` license already ships). ✅

**Type consistency:** `AiSource` (Byo/PlatformManaged), `PlatformLlmOptions.CreditTokenRatio` (long), `UsageUnit.Tokens`, `TenantQuota.AiCreditsMonthly` (long?), `UsageType.AiAnalysis` (16), `IMeteringService.RecordBatchAsync`, `ITypificationCreditMeter.RecordAsync` — names match across tasks. `UsageRecord.Quantity` is `decimal` (token int widens). `LlmUsage` is `(int PromptTokens, int CompletionTokens, int TotalTokens)` — matched in B3/C3.

**Open tunables (finalize during execution, not load-bearing):** `CreditTokenRatio` default 1000; the SoftBlock pre-check uses `additionalQuantity: 1m` (nominal — blocks an at-limit tenant); Warn threshold is surfaced via `QuotaCheckResult.UsagePercent` (no separate config needed). The `BillingTypificationCreditMeter` takes `IClock` for a testable `RecordedAt` (adjust the B3 test ctor accordingly).
