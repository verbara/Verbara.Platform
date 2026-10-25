using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Pro.Dialer.Campaign;

namespace Verbara.Platform.Api.Services;

/// <summary>What one hand-off of a dialer disposition dispatch came to.</summary>
public enum DispatchOutcome
{
    /// <summary>The dialer applied the disposition (<see cref="AgentDispositionResult.Applied"/>).</summary>
    Applied,

    /// <summary>The dialer had already accepted a disposition for the call (<see cref="AgentDispositionResult.AlreadyApplied"/>).</summary>
    AlreadyApplied,

    /// <summary>The dialer keeps it until the call's outcome (<see cref="AgentDispositionResult.AwaitingOutcome"/>).</summary>
    AwaitingOutcome,

    /// <summary>The dialer knows no such call (<see cref="AgentDispositionResult.UnknownCall"/>).</summary>
    UnknownCall,

    /// <summary>The hand-off threw or was cancelled; the row was released and is handed over again.</summary>
    Deferred,
}

/// <summary>
/// Hands one stored typification's disposition to the dialer (dialer-honours-dnc-callbacks-and-agent-dispositions,
/// design D16): the one routine the typify request and <see cref="DialerDispositionDispatchWorker"/> share. It calls
/// <see cref="CampaignStoreBase.ApplyAgentDispositionAsync"/>; on any of the four answers it completes the row, fenced
/// on the claim, and only when that completion succeeded it announces <see cref="CampaignDispositionSubmittedEvent"/>
/// per the event rule; on an exception or a cancellation it releases the row, fenced on the claim too, with a capped
/// backoff and no cap on the retries (owner Q9: a disposition is never dropped).
/// </summary>
public sealed partial class DialerDispositionDispatcher
{
    /// <summary>The claim's lease: a hand-off that has not answered within it may be taken over by any dispatcher.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan BackoffStep = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BackoffCap = TimeSpan.FromMinutes(5);

    /// <summary>From this claim on, every release logs a warning naming the Conversation (owner Q9).</summary>
    internal const int WarnFromAttempt = 10;

    private readonly IServiceProvider _services;
    private readonly IDialerDispositionDispatchStore _store;
    private readonly PlatformEventBus _eventBus;
    private readonly IClock _clock;
    private readonly DialerDispositionMetrics _metrics;
    private readonly ILogger<DialerDispositionDispatcher> _logger;

    public DialerDispositionDispatcher(
        IServiceProvider services,
        IDialerDispositionDispatchStore store,
        PlatformEventBus eventBus,
        IClock clock,
        DialerDispositionMetrics metrics,
        ILogger<DialerDispositionDispatcher> logger)
    {
        _services = services;
        _store = store;
        _eventBus = eventBus;
        _clock = clock;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Builds the row a typification of a campaign call stores with its submission, pre-claimed by the request: its own
    /// claim id, a lease from the Api's clock, one attempt, due now.
    /// </summary>
    public DialerDispositionDispatch NewPreClaimed(DialerDispositionDispatch template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var now = _clock.UtcNow;
        return template with
        {
            Attempts = 1,
            NextAttemptAt = now,
            ClaimId = Guid.NewGuid().ToString("N"),
            ClaimExpiresAt = now + Lease,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Hands <paramref name="row"/>, held by <paramref name="claimId"/>, to the dialer and records the answer. Never
    /// throws for a failed hand-off: a failure releases the row and answers <see cref="DispatchOutcome.Deferred"/>.
    /// </summary>
    public async Task<DispatchOutcome> DispatchAsync(DialerDispositionDispatch row, string claimId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrEmpty(claimId);

        AgentDispositionResult result;
        try
        {
            var campaigns = _services.GetService<CampaignStoreBase>()
                            ?? throw new InvalidOperationException("No dialer campaign store is registered.");
            result = await campaigns.ApplyAgentDispositionAsync(
                new AgentDisposition
                {
                    TenantId = row.TenantId,
                    CampaignId = row.CampaignId,
                    ActionId = row.ActionId,
                    DialerContactId = row.DialerContactId,
                    DispositionCodeId = row.DispositionCodeId,
                    Comment = row.Comment,
                    CallbackAt = row.CallbackAt,
                    RequestsCallback = row.RequestsCallback,
                    AgentId = row.AgentId,
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure of the hand-off, a cancellation included, releases the row for another attempt.
            await ReleaseAsync(row, claimId, ex).ConfigureAwait(false);
            return DispatchOutcome.Deferred;
        }

        // The answer is recorded whatever the request's token says: the dialer already acted on it.
        var published = result is AgentDispositionResult.Applied or AgentDispositionResult.AwaitingOutcome
                        || (result == AgentDispositionResult.AlreadyApplied && row.Attempts > 1);
        var completed = await _store.CompleteAsync(row, result.ToString(), published, claimId, CancellationToken.None)
            .ConfigureAwait(false);
        if (completed)
        {
            _metrics.RecordCompleted(result.ToString());
            if (published)
                _eventBus.Publish(new CampaignDispositionSubmittedEvent(
                    row.TenantId, row.CampaignId, row.DispositionCode, row.AgentId ?? ""));
        }
        else
        {
            LogCompletionTakenOver(row.ConversationId, result);
        }

        if (result == AgentDispositionResult.UnknownCall)
            LogUnknownCall(row.CampaignId, row.DispositionCode, row.ConversationId);

        return result switch
        {
            AgentDispositionResult.Applied => DispatchOutcome.Applied,
            AgentDispositionResult.AlreadyApplied => DispatchOutcome.AlreadyApplied,
            AgentDispositionResult.AwaitingOutcome => DispatchOutcome.AwaitingOutcome,
            _ => DispatchOutcome.UnknownCall,
        };
    }

    /// <summary>The state the typify response reports for <paramref name="outcome"/> (owner Q8).</summary>
    public static DialerDispositionState ToState(DispatchOutcome outcome) => outcome switch
    {
        DispatchOutcome.Applied => DialerDispositionState.Applied,
        DispatchOutcome.AlreadyApplied => DialerDispositionState.AlreadyApplied,
        DispatchOutcome.AwaitingOutcome => DialerDispositionState.AwaitingOutcome,
        DispatchOutcome.UnknownCall => DialerDispositionState.UnknownCall,
        _ => DialerDispositionState.Pending,
    };

    private async Task ReleaseAsync(DialerDispositionDispatch row, string claimId, Exception ex)
    {
        var backoff = TimeSpan.FromTicks(Math.Min(BackoffStep.Ticks * Math.Max(1, row.Attempts), BackoffCap.Ticks));
        var error = $"{ex.GetType().Name}: {ex.Message}";
        bool released;
        try
        {
            // On None: the request's token may be the reason the hand-off failed.
            released = await _store.ReleaseAsync(row, error, _clock.UtcNow + backoff, claimId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception releaseEx) when (releaseEx is not OutOfMemoryException)
        {
            // The claim's lease still passes, so any dispatcher takes the row over then.
            LogReleaseFailed(row.ConversationId, releaseEx);
            return;
        }

        if (!released)
            return;

        _metrics.RecordReleased();
        if (row.Attempts >= WarnFromAttempt)
            LogStillPending(row.ConversationId, row.Attempts, error);
        else
            LogReleased(row.ConversationId, row.Attempts, error);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[DIALER-DISPOSITION] Campaign {CampaignId} knows no call for the disposition {Code} of Conversation {ConversationId}; nothing applied.")]
    private partial void LogUnknownCall(long campaignId, string code, string conversationId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[DIALER-DISPOSITION] Conversation {ConversationId}: the dialer answered {Result}, but another claim had taken the dispatch over; it records the answer.")]
    private partial void LogCompletionTakenOver(string conversationId, AgentDispositionResult result);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[DIALER-DISPOSITION] Conversation {ConversationId}: hand-off {Attempt} failed ({Error}); it is handed over again.")]
    private partial void LogReleased(string conversationId, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[DIALER-DISPOSITION] Conversation {ConversationId}: the dialer has not taken its disposition after {Attempt} hand-offs ({Error}); it is handed over again.")]
    private partial void LogStillPending(string conversationId, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[DIALER-DISPOSITION] Conversation {ConversationId}: the failed hand-off could not be released; it is taken over when its claim expires.")]
    private partial void LogReleaseFailed(string conversationId, Exception ex);
}
