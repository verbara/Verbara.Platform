using System.Collections.Concurrent;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Email;

/// <summary>
/// In-memory implementation of <see cref="IEmailThreadingContext"/>.
/// Tracks sent message IDs per conversation so replies include proper
/// <c>In-Reply-To</c> and <c>References</c> headers.
/// </summary>
public sealed class InMemoryEmailThreadingContext : IEmailThreadingContext
{
    // conversationId → list of sent message IDs (oldest first)
    private readonly ConcurrentDictionary<string, List<string>> _sent = new();
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public EmailThreadInfo? GetThreadInfo(EntityId conversationId)
    {
        if (!_sent.TryGetValue(conversationId.Value, out var ids))
            return null;

        lock (_lock)
        {
            if (ids.Count == 0) return null;

            var inReplyTo = ids[^1];
            var references = string.Join(" ", ids);
            return new EmailThreadInfo(inReplyTo, references);
        }
    }

    /// <inheritdoc />
    public void RecordSentMessage(EntityId conversationId, string messageId)
    {
        var ids = _sent.GetOrAdd(conversationId.Value, _ => []);
        lock (_lock)
        {
            ids.Add(messageId);
        }
    }
}
