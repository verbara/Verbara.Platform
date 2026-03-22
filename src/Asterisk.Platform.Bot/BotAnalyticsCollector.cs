using System.Reactive.Subjects;

namespace Asterisk.Platform.Bot;

/// <summary>
/// Collects <see cref="BotAnalyticsEvent"/> instances emitted by the bot orchestrator
/// and exposes them as a push-based observable stream.
/// </summary>
public sealed class BotAnalyticsCollector : IDisposable
{
    private readonly Subject<BotAnalyticsEvent> _subject = new();

    /// <summary>
    /// An observable stream of all bot analytics events.
    /// </summary>
    public IObservable<BotAnalyticsEvent> Events => _subject;

    /// <summary>
    /// Publishes a new analytics event to all subscribers.
    /// </summary>
    internal void Emit(BotAnalyticsEvent evt) => _subject.OnNext(evt);

    /// <inheritdoc />
    public void Dispose() => _subject.Dispose();
}
