using System.Text.Json.Serialization;
using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

public sealed class Agent : ITenantScoped, IAuditable
{
    public required EntityId AgentId { get; init; }
    public required TenantId TenantId { get; init; }
    public required EntityId UserId { get; init; }
    public required string DisplayName { get; set; }
    public required AgentState State { get; set; }

    /// <summary>W4 — deferred ("pause-when-free") target. When set, the agent has
    /// requested this aux state; it is applied only once active work drains (or the
    /// per-tenant timeout forces it). Requesting a pause does NOT change State — it
    /// records the target here so State keeps reflecting the real (still-working) state.</summary>
    public AgentState? PendingState { get; set; }
    public string? PendingReason { get; set; }
    public DateTimeOffset? PendingSince { get; set; }

    /// <summary>
    /// W5 — when the agent ENTERED the Offline state (UTC), or null when not Offline.
    /// The work-failover sweep uses (now - OfflineSince) to measure the grace before
    /// re-queueing the agent's orphaned conversations. Set on entering Offline (and
    /// NOT reset by repeated offline writes, e.g. idle beacons), cleared on leaving.
    /// </summary>
    public DateTimeOffset? OfflineSince { get; set; }

    public bool HasPendingPause => PendingState is not null;

    /// <summary>
    /// W6 — sparse per-agent capacity override. Each null field inherits the
    /// per-tenant default; the effective <see cref="ChannelCapacity"/> is resolved
    /// at read time by IAgentCapacityResolver (tenant default + this override).
    /// Persisted as the <c>agents.capacity</c> jsonb column (legacy <c>'{}'</c>
    /// rows deserialize to an all-null override = "inherit everything").
    /// </summary>
    public ChannelCapacityOverride CapacityOverride { get; set; } = new();
    public EntityId? TeamId { get; set; }
    public IReadOnlyList<string> Skills { get; set; } = [];
    public string? Extension { get; set; }

    // The plaintext SIP password is a secret (required plaintext by Asterisk
    // realtime ps_auths). It must NEVER serialize from the entity over HTTP —
    // the ONLY deliberate exposure is the self-scoped AgentMeResponseDto on
    // GET /agents/me, which copies it into its own field in C#. JsonIgnore here
    // is defense-in-depth so admin/list endpoints returning the raw entity
    // (or any future endpoint) can never leak it.
    [JsonIgnore]
    public string? SipPassword { get; set; }

    // Auto-answer override for the in-browser softphone (3B.2b). Tri-state on
    // purpose: null = "inherit the queue default" (the call's queue carries
    // AutoAnswerDefault), true/false = an explicit per-agent override. The
    // effective decision is computed client-side from this + the screen-pop's
    // queue default. Not a secret — serializes normally.
    public bool? AutoAnswer { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }

    public bool CanAcceptWork => AgentStateMachine.IsRoutable(State);

    /// <summary>
    /// W6 — a pure routability check. The per-channel / MaxTotal capacity gate now
    /// requires the resolved effective <see cref="ChannelCapacity"/> (tenant default
    /// merged with <see cref="CapacityOverride"/>), which is not available on the
    /// agent entity alone — that gate lives in IAgentCapacityService + the resolver
    /// (W6-A3/A4). This helper only answers "is the agent in a routable state?".
    /// The <paramref name="channel"/> arg is retained for source-compatibility and
    /// future per-channel routability rules.
    /// </summary>
    public bool HasCapacity(ChannelType channel)
    {
        _ = channel;
        return CanAcceptWork;
    }

    public void TransitionTo(AgentState newState, DateTimeOffset? now = null)
    {
        AgentStateMachine.EnsureTransition(State, newState);
        State = newState;
        // W5 — entering Offline stamps the grace clock (preserving an existing
        // stamp so repeated offline writes don't reset it); any non-Offline
        // target clears it.
        OfflineSince = newState == AgentState.Offline
            ? (OfflineSince ?? now ?? DateTimeOffset.UtcNow)
            : null;
    }

    // Bounded teardown helper: forces Offline from ANY state, bypassing
    // EnsureTransition ON PURPOSE. Reserved for sign-off / disconnect teardown
    // (e.g. idle-logout, dropped connections) where leaving an agent in a
    // routable state would create a routing zombie. NOT for user-driven
    // UpdateAgentState — that must keep going through TransitionTo so normal
    // transition validation still applies. The single Offline target is the
    // only transition this loosens.
    public void ForceOffline(DateTimeOffset? now = null)
    {
        State = AgentState.Offline;
        // W5 — stamp the grace clock once; the ??= means a repeated ForceOffline
        // (e.g. an idle beacon followed by the liveness reaper) does NOT reset it.
        OfflineSince ??= now ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Applies the deferred PendingState as the new State, bypassing EnsureTransition
    /// ON PURPOSE (e.g. ACW->Lunch is invalid in the table, but the target was
    /// validated as a legitimate aux state at request time). Mirrors the bounded
    /// ForceOffline() bypass. Idempotent no-op when no pending. Does NOT widen the
    /// public transition table — user-driven UpdateAgentState still uses TransitionTo.
    /// </summary>
    public void ApplyPendingState()
    {
        if (PendingState is null) return;
        // W5 — OfflineSince is irrelevant here: a pending pause only ever targets
        // an aux state (never Offline) from a working state, so the grace clock
        // stays null throughout and needs no touch.
        State = PendingState.Value;
        PendingState = null;
        PendingReason = null;
        PendingSince = null;
    }
}
