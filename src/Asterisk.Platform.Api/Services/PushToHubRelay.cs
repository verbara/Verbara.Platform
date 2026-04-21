using Asterisk.Sdk.Pro.Push.SignalR.Events;
using Asterisk.Sdk.Pro.Push.SignalR.Hubs;
using Asterisk.Sdk.Push.Bus;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Api.Services;

// ---------------------------------------------------------------------------
// Log messages
// ---------------------------------------------------------------------------
internal static partial class PushToHubRelayLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[RELAY] Skipping event {EventType}: TenantId is null or empty.")]
    public static partial void SkippedNullTenant(ILogger logger, string eventType);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[RELAY] Forwarded {EventType} for tenant={TenantId} to SignalR group.")]
    public static partial void Forwarded(ILogger logger, string eventType, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[RELAY] Error forwarding {EventType}: {Reason}")]
    public static partial void ForwardError(ILogger logger, string eventType, string reason);
}

// ---------------------------------------------------------------------------
// Wire payloads (AOT-friendly records, serialised via ApiJsonContext below)
// ---------------------------------------------------------------------------

/// <summary>Payload sent to SignalR clients for conversation state transitions.</summary>
public sealed record ConversationStateChangedPayload(
    string ConversationId,
    string PreviousState,
    string NewState,
    DateTimeOffset ChangedAt,
    string TenantId);

/// <summary>Payload sent to SignalR clients for agent state transitions.</summary>
public sealed record AgentStateChangedPayload(
    string AgentId,
    string PreviousState,
    string NewState,
    string? ReasonCode,
    DateTimeOffset ChangedAt,
    string TenantId);

// ---------------------------------------------------------------------------
// Service
// ---------------------------------------------------------------------------

/// <summary>
/// Bridges the in-process <see cref="IPushEventBus"/> T27 events to the
/// SignalR <see cref="PlatformHub"/> so connected clients receive real-time
/// conversation and agent state transitions.
///
/// Subscribes to <see cref="ConversationStateChangedEvent"/> and
/// <see cref="AgentStateChangedEvent"/> on <see cref="StartAsync"/> and disposes
/// the subscriptions on <see cref="StopAsync"/>. Events whose
/// <c>Metadata.TenantId</c> is null or empty are silently skipped (one warning
/// is emitted per occurrence via the <c>[LoggerMessage]</c> source generator).
/// </summary>
public sealed class PushToHubRelay : IHostedService
{
    private readonly IPushEventBus _bus;
    private readonly IHubContext<PlatformHub> _hubContext;
    private readonly ILogger<PushToHubRelay> _logger;

    private IDisposable? _conversationSubscription;
    private IDisposable? _agentSubscription;

    /// <summary>Creates a new relay instance.</summary>
    public PushToHubRelay(
        IPushEventBus bus,
        IHubContext<PlatformHub> hubContext,
        ILogger<PushToHubRelay> logger)
    {
        _bus = bus;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _conversationSubscription = _bus.OfType<ConversationStateChangedEvent>()
            .Subscribe(evt => ForwardConversation(evt));

        _agentSubscription = _bus.OfType<AgentStateChangedEvent>()
            .Subscribe(evt => ForwardAgent(evt));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _conversationSubscription?.Dispose();
        _agentSubscription?.Dispose();
        _conversationSubscription = null;
        _agentSubscription = null;
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Internal forwarding helpers (fire-and-forget; errors logged, not thrown)
    // -----------------------------------------------------------------------

    private void ForwardConversation(ConversationStateChangedEvent evt)
    {
        var tenantId = evt.Metadata?.TenantId;
        if (string.IsNullOrEmpty(tenantId))
        {
            PushToHubRelayLog.SkippedNullTenant(_logger, evt.EventType);
            return;
        }

        var payload = new ConversationStateChangedPayload(
            ConversationId: evt.ConversationId,
            PreviousState: evt.PreviousState,
            NewState: evt.NewState,
            ChangedAt: evt.ChangedAt,
            TenantId: tenantId);

        _ = SendAsync($"tenant:{tenantId}", "OnConversationStateChanged", payload, tenantId, evt.EventType);
    }

    private void ForwardAgent(AgentStateChangedEvent evt)
    {
        var tenantId = evt.Metadata?.TenantId;
        if (string.IsNullOrEmpty(tenantId))
        {
            PushToHubRelayLog.SkippedNullTenant(_logger, evt.EventType);
            return;
        }

        var payload = new AgentStateChangedPayload(
            AgentId: evt.AgentId,
            PreviousState: evt.PreviousState,
            NewState: evt.NewState,
            ReasonCode: evt.ReasonCode,
            ChangedAt: evt.ChangedAt,
            TenantId: tenantId);

        _ = SendAsync($"tenant:{tenantId}", "OnAgentStateChanged", payload, tenantId, evt.EventType);
    }

    private async Task SendAsync(string group, string method, object payload, string tenantId, string eventType)
    {
        try
        {
            await _hubContext.Clients.Group(group)
                .SendCoreAsync(method, [payload], CancellationToken.None)
                .ConfigureAwait(false);

            PushToHubRelayLog.Forwarded(_logger, eventType, tenantId);
        }
        catch (Exception ex)
        {
            PushToHubRelayLog.ForwardError(_logger, eventType, ex.Message);
        }
    }
}
