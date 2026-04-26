# ADR-0007: Agent tenant resolver strict mode builder

- **Status:** Accepted (executed during R5.3 Phase 0)
- **Date:** 2026-04-26
- **Deciders:** Harold Reina
- **Related:**
  - ADR-0005 `cross-tenant-signalr-subscription-validation` (parent — defines `IAgentTenantResolver` contract)
  - R5.3 spec §"D-FORCE-1" (`docs/plans/active/2026-04-26-r5.3-admin-completeness-r4-closure.md`)
  - Post-R5.2 deep audit findings (Agent 2) — confirmed `IAgentTenantResolver?` injection is optional with null default
  - R5.2 known-debt #2 (carried into R5.3 Set B)

## Context

ADR-0005 (R5.2 Phase 0, Accepted) introduced `IAgentTenantResolver` to validate cross-tenant SignalR subscriptions in `PlatformHub.SubscribeToAgentPresenceAsync`. The implementation pattern shipped in Pro 1.13.0-pro injects the resolver as **optional** with a null default at `Asterisk.Sdk.Pro.Push.SignalR/Hubs/PlatformHub.cs:70-82`:

```csharp
private readonly IAgentTenantResolver? _tenantResolver;

public PlatformHub(
    // ... other deps ...
    IAgentTenantResolver? tenantResolver = null)  // ← optional, default null
{
    _tenantResolver = tenantResolver;
}
```

The subscription validation guard at line 268 reads:

```csharp
if (_tenantResolver is not null) {
    // ... enforce cross-tenant check + audit on denial ...
}
// else: legacy permissive behavior (subscription proceeds unchecked)
```

**Production safety implication:** A host that forgets to register `IAgentTenantResolver` in DI gets the **legacy permissive behavior silently**. There is no warning, no log, no error — cross-tenant subscriptions just succeed. ADR-0005 §"Concerns" documents this trade-off but provides no remediation path.

**Why optional was chosen in R5.2:** Backwards compatibility. Pro 1.13.0-pro was a minor bump per Pro roadmap §Principios ("0 breaking changes en minors"). Forcing required injection would have broken every host that hadn't yet adopted the resolver pattern.

**Why this is now a problem:** Production deployments that should be using the resolver may not be — and they have no signal indicating the gap. Operators don't know what they don't know. The R5.2 ship narrative emphasized "SOC 2 baseline" but a subset of hosts may be running with zero cross-tenant protection.

**Concrete evidence:**

- `Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Push.SignalR/Hubs/PlatformHub.cs:70` — `private readonly IAgentTenantResolver? _tenantResolver;` (nullable field).
- `Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Push.SignalR/Hubs/PlatformHub.cs:82` — constructor parameter `IAgentTenantResolver? tenantResolver = null`.
- `Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Push.SignalR/Hubs/PlatformHub.cs:268` — `if (_tenantResolver is not null) { ... }` guard with no `else` warning branch.
- ADR-0005 §"Concerns" (line 252-255 of ADR-0005) explicitly notes: *"Hosts that do not register an IAgentTenantResolver get the legacy permissive behavior (no tenant check) — registering a resolver is the production-ready configuration."*

## Decision

**Add a fluent builder extension `WithStrictMode(bool enabled = true)` on `ProPushBridgeBuilder` that registers `IPlatformHubStrictModePolicy` singleton. `PlatformHub.OnConnectedAsync` consults the policy:**

- **If `policy.StrictModeEnabled && _tenantResolver is null`** → throw `InvalidOperationException` with explicit message ("Strict mode requires IAgentTenantResolver registration. Register the resolver via DI or remove WithStrictMode() opt-in."). Hub fails-fast at connection time, not at first subscription.
- **Else if `_tenantResolver is null` && multi-tenant context detected** → emit a warning log: *"PlatformHub: IAgentTenantResolver not registered — cross-tenant subscriptions are permissive (legacy behavior). Register resolver via DI or call .WithStrictMode() on the bridge builder to fail-fast."* (one-time per Hub instance, not per connection).
- **Else** → proceed with R5.2 behavior (resolver enforces if present, permissive if absent).

### API shape

```csharp
// Pro.Push.SignalR/Authz/IPlatformHubStrictModePolicy.cs (NEW)
namespace Asterisk.Sdk.Pro.Push.SignalR.Authz;

public interface IPlatformHubStrictModePolicy
{
    bool StrictModeEnabled { get; }
}

internal sealed record PlatformHubStrictModePolicy(bool StrictModeEnabled)
    : IPlatformHubStrictModePolicy;

// Pro.Push.SignalR/DependencyInjection/ProPushBridgeBuilderExtensions.cs (modify)
public static ProPushBridgeBuilder WithStrictMode(
    this ProPushBridgeBuilder builder,
    bool enabled = true)
{
    builder.Services.AddSingleton<IPlatformHubStrictModePolicy>(
        new PlatformHubStrictModePolicy(StrictModeEnabled: enabled));
    return builder;
}

// Pro.Push.SignalR/Hubs/PlatformHub.cs (modify constructor + OnConnectedAsync)
private readonly IPlatformHubStrictModePolicy _strictModePolicy;
private readonly IAgentTenantResolver? _tenantResolver;
private readonly ITenantContext? _tenantContext;
private readonly ILogger<PlatformHub> _logger;

public PlatformHub(
    // ... existing deps ...
    IAgentTenantResolver? tenantResolver = null,
    IPlatformHubStrictModePolicy? strictModePolicy = null,
    ITenantContext? tenantContext = null,
    ILogger<PlatformHub>? logger = null)
{
    _tenantResolver = tenantResolver;
    _strictModePolicy = strictModePolicy ?? new PlatformHubStrictModePolicy(false);
    _tenantContext = tenantContext;
    _logger = logger ?? NullLogger<PlatformHub>.Instance;
}

public override async Task OnConnectedAsync()
{
    if (_strictModePolicy.StrictModeEnabled && _tenantResolver is null)
    {
        throw new InvalidOperationException(
            "PlatformHub strict mode requires IAgentTenantResolver registration. " +
            "Either register the resolver via DI or remove WithStrictMode() opt-in.");
    }

    if (_tenantResolver is null && IsMultiTenantContext())
    {
        _logger.LogWarning(
            "PlatformHub: IAgentTenantResolver not registered — cross-tenant subscriptions are permissive (legacy behavior). " +
            "Register resolver via DI or call .WithStrictMode() on the bridge builder to fail-fast.");
    }

    await base.OnConnectedAsync();
}

private bool IsMultiTenantContext()
    => _tenantContext?.IsMultiTenant ?? false;
```

### Migration guide for hosts

R5.3 release notes will recommend:

```csharp
// Recommended for production deployments (R5.3+):
builder.Services.AddProPushBridges()
    .WithClusterEventBridge()
    .WithConversationBridge()
    .WithAgentBridge()
    .WithDefaultTenantId("default")
    .WithStrictMode();   // ← NEW: fail-fast if resolver missing

// Also register the resolver:
builder.Services.AddSingleton<IAgentTenantResolver, MyAgentTenantResolver>();
```

R5.4 will flip the default behavior — `WithStrictMode()` will become opt-out via `WithLegacyPermissiveMode()` instead of opt-in. Operators who saw the warning log in R5.3 will have already migrated; the R5.4 default-flip is then a non-event for them.

## Decision space

**(a) Required-by-default in Pro 1.14.0-pro:** Constructor signature changes to `IAgentTenantResolver tenantResolver` (non-nullable, no default value). Breaking change for hosts not yet registering. Rejected because:
- Violates "0 breaking changes en minors" principle established in Pro roadmap §Principios.
- Forces operators to ship a resolver registration in the same release window — no time to test in their environment first.
- Cannot detect via deprecation warning (compile-time break).

**(b) `WithStrictMode()` builder opt-in + warning log (chosen):**
- Preserves backwards compatibility (R5.3 ships as minor bump per SemVer).
- Provides an explicit opt-in path for operators ready for strict enforcement.
- Warning log alerts operators who haven't opted in yet — they have observability into the gap before R5.4 default-flip.
- R5.4 can flip the default with operators already aware (no surprise breaking).

**(c) Acceptable status quo:** Leave optional injection; add only documentation. Rejected because:
- Documentation is not enforcement. Operators who don't read the docs continue running unprotected.
- Contradicts user directive "no atajos, todo en pro del producto final."
- ADR-0005 already documents the concern; adding more docs doesn't change behavior.

## Consequences

**Positivas:**
- Backwards compat preserved in R5.3 (minor bump, additive API surface).
- Production safety opt-in available immediately for operators ready for it.
- Warning log provides operational telemetry — operators can monitor adoption via log aggregation (e.g., grep for "IAgentTenantResolver not registered" across deployment fleet).
- R5.4 default-flip becomes a non-event for operators who already opted into strict mode (or who responded to the warning log).
- Aligned with semver discipline + R5 production-readiness narrative.

**Negativas:**
- Opt-in still missable — operators who never read release notes continue unprotected until R5.4 default-flip.
- Warning log adds noise for single-tenant hosts (mitigated by `IsMultiTenantContext()` heuristic — log only emits when `ITenantContext.IsMultiTenant` is true).
- Adds another constructor parameter to `PlatformHub` (still optional with sensible default).
- Adds another DI extension method to learn (`WithStrictMode`).

## Alternatives considered

See **Decision space** above.

## References

- ADR-0005 `cross-tenant-signalr-subscription-validation.md`
- R5.3 spec: `docs/plans/active/2026-04-26-r5.3-admin-completeness-r4-closure.md` §"D-FORCE-1"
- Code: `Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Push.SignalR/Hubs/PlatformHub.cs:70-82,253-298`
- Pro roadmap §Principios (`Asterisk.Sdk.Pro/docs/roadmap.md` §"Principios de planificación" #2 — "0 breaking changes en minors")
- R5.3 execution plan task A.2: `docs/plans/active/2026-04-26-r5.3-execution-plan.md`
