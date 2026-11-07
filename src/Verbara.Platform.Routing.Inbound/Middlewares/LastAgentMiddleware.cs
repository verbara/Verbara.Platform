using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Routing.Inbound.Middlewares;

/// <summary>
/// Last-agent routing (conversations-return-to-last-agent design D10): runs the rest of the chain first, then, for the
/// route's final queue, names the agent who last owned one of the contact's conversations as the route's preferred
/// agent. Registered just inside <see cref="ReasonHintMiddleware"/>, outside every middleware that can change the queue,
/// so the queue it reads is the queue the caller assigns.
/// <list type="number">
///   <item>The switch is <c>queue.LastAgentRoutingEnabled ?? host</c> for the final queue (D2); a queue missing from the
///   store reads as <c>NULL</c>. Off → <c>disabled</c>.</item>
///   <item>A shared placeholder contact, or a WebChat contact whose identity level is <c>Unverified</c>, gets no
///   preference → <c>skipped-identity</c> (D7).</item>
///   <item>The lookup reaches back <c>max(WindowHours, the tenant's effective inactivity window)</c> while the inactivity
///   close is on, else <c>WindowHours</c> (D4, the clamp) → <c>stamped</c> or <c>no-history</c>.</item>
/// </list>
/// Every read this adds to the route is caught (D6): a failure logs a warning, counts <c>lookup-failed</c> and returns
/// the route unchanged, so the feature never fails a route, even while it is off. Request cancellation propagates.
/// When the continuation throws or returns no route, nothing is counted (D8). One routing-stage outcome is counted per
/// routing attempt that returned a route: a conversation routed twice counts twice, by design (D8).
/// </summary>
public sealed partial class LastAgentMiddleware : InboundRoutingMiddlewareBase
{
    private readonly IConversationStore _conversationStore;
    private readonly IQueueStore _queueStore;
    private readonly IInactivityWindowResolver _inactivityWindow;
    private readonly LastAgentRoutingOptions _options;
    private readonly ConversationOptions _conversationOptions;
    private readonly RoutingMetrics _metrics;
    private readonly IClock _clock;
    private readonly ILogger<LastAgentMiddleware> _logger;

    public LastAgentMiddleware(
        IConversationStore conversationStore,
        IQueueStore queueStore,
        IInactivityWindowResolver inactivityWindow,
        IOptions<LastAgentRoutingOptions> options,
        IOptions<ConversationOptions> conversationOptions,
        RoutingMetrics metrics,
        IClock clock,
        ILogger<LastAgentMiddleware> logger)
    {
        _conversationStore = conversationStore;
        _queueStore = queueStore;
        _inactivityWindow = inactivityWindow;
        _options = options.Value;
        _conversationOptions = conversationOptions.Value;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
    }

    public override async Task<RouteResult?> RouteAsync(
        RoutingContext context,
        Func<Task<RouteResult?>> continuation,
        CancellationToken ct)
    {
        // Continuation first, outside the catch: a router failure is the caller's, and counts nothing (D8).
        var result = await continuation().ConfigureAwait(false);
        if (result is null)
            return null;

        var tenantId = context.TenantId.Value;
        var queueId = result.QueueId.Value;
        try
        {
            var queue = await _queueStore.GetByIdAsync(context.TenantId, result.QueueId, ct).ConfigureAwait(false);
            if (!(queue?.LastAgentRoutingEnabled ?? _options.Enabled))
            {
                _metrics.RecordLastAgent(LastAgentOutcomes.Disabled);
                return result;
            }

            if (IsIdentityTooWeak(context))
            {
                _metrics.RecordLastAgent(LastAgentOutcomes.SkippedIdentity);
                LogSkippedIdentity(tenantId, queueId);
                return result;
            }

            var windowHours = _options.WindowHours;
            if (_conversationOptions.InactivityCloseEnabled)
            {
                var inactivity = await _inactivityWindow.ResolveAsync(context.TenantId, ct).ConfigureAwait(false);
                windowHours = Math.Max(windowHours, inactivity.Hours);
            }

            var since = _clock.UtcNow - TimeSpan.FromHours(windowHours);
            var owner = await _conversationStore
                .FindLastAgentOwnerAsync(context.TenantId, context.Contact.ContactId, since, ct)
                .ConfigureAwait(false);

            if (owner is not { } agentId)
            {
                _metrics.RecordLastAgent(LastAgentOutcomes.NoHistory);
                LogNoHistory(tenantId, queueId, windowHours);
                return result;
            }

            _metrics.RecordLastAgent(LastAgentOutcomes.Stamped);
            LogStamped(tenantId, queueId, agentId.Value, windowHours);
            return result with { PreferredAgentId = agentId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _metrics.RecordLastAgent(LastAgentOutcomes.LookupFailed);
            LogLookupFailed(tenantId, queueId, ex.GetType().Name);
            return result;
        }
    }

    /// <summary>Design D7: one contact for many people, or an unsigned WebChat visitor id a browser can edit.</summary>
    private static bool IsIdentityTooWeak(RoutingContext context) =>
        SharedContactAddresses.IsShared(context.Contact.Addresses)
        || (context.Channel == ChannelType.WebChat && context.Contact.IdentityLevel == ContactIdentityLevel.Unverified);

    [LoggerMessage(EventId = 7701, Level = LogLevel.Information,
        Message = "[LAST-AGENT] tenant {TenantId} queue {QueueId}: preferred agent {AgentId} (window {WindowHours} h)")]
    private partial void LogStamped(string tenantId, string queueId, string agentId, int windowHours);

    [LoggerMessage(EventId = 7702, Level = LogLevel.Information,
        Message = "[LAST-AGENT] tenant {TenantId} queue {QueueId}: no previous agent in the last {WindowHours} h")]
    private partial void LogNoHistory(string tenantId, string queueId, int windowHours);

    [LoggerMessage(EventId = 7703, Level = LogLevel.Information,
        Message = "[LAST-AGENT] tenant {TenantId} queue {QueueId}: no preference for a shared or unverified contact")]
    private partial void LogSkippedIdentity(string tenantId, string queueId);

    // The exception type only: a database error can quote the row.
    [LoggerMessage(EventId = 7704, Level = LogLevel.Warning,
        Message = "[LAST-AGENT] tenant {TenantId} queue {QueueId}: last-agent lookup failed ({ExceptionType}); routed with no preference")]
    private partial void LogLookupFailed(string tenantId, string queueId, string exceptionType);
}
