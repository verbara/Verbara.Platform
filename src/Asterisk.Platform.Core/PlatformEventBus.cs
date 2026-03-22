using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Asterisk.Platform.Core;

/// <summary>
/// Lightweight in-process event bus for broadcasting platform events to SSE subscribers.
/// </summary>
public sealed class PlatformEventBus : IDisposable
{
    private readonly Subject<PlatformEvent> _events = new();

    /// <summary>
    /// Observable stream of all platform events.
    /// </summary>
    public IObservable<PlatformEvent> Events => _events.AsObservable();

    /// <summary>
    /// Publishes an event to all subscribers.
    /// </summary>
    public void Publish(PlatformEvent evt) => _events.OnNext(evt);

    public void Dispose() => _events.Dispose();
}

/// <summary>
/// Base record for all platform events distributed via <see cref="PlatformEventBus"/>.
/// </summary>
public abstract record PlatformEvent(string TenantId, string Type, DateTimeOffset Timestamp);

/// <summary>Raised when a conversation is assigned to an agent.</summary>
public sealed record ConversationAssignedEvent(
    string TenantId,
    string ConversationId,
    string AgentId,
    string QueueName,
    string Channel,
    string ContactName)
    : PlatformEvent(TenantId, "conversation.assigned", DateTimeOffset.UtcNow);

/// <summary>Raised when a new message arrives in a conversation.</summary>
public sealed record ConversationMessageEvent(
    string TenantId,
    string ConversationId,
    string MessageId,
    string Sender,
    string Text)
    : PlatformEvent(TenantId, "conversation.message", DateTimeOffset.UtcNow);

/// <summary>Raised when a conversation changes state.</summary>
public sealed record ConversationStateChangedEvent(
    string TenantId,
    string ConversationId,
    string OldState,
    string NewState)
    : PlatformEvent(TenantId, "conversation.state_changed", DateTimeOffset.UtcNow);
