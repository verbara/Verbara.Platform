using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

/// <summary>
/// The keyset position a history page ends at (contact-history-opens-by-assignment, design D10): the last item's
/// <c>created_at</c> and message id. A history read with it returns only messages strictly older, by
/// (<c>created_at</c>, message id ordinal). Not a wire shape: the Api encodes it as an opaque cursor.
/// </summary>
public readonly record struct MessageHistoryCursor(DateTimeOffset CreatedAt, EntityId MessageId);

/// <summary>
/// One newest-first page of a conversation's messages (design D10): at most the requested limit, ordered
/// <c>created_at</c> descending then message id descending (ordinal), and whether an older message exists past the
/// last item (the store reads one row more than the limit to know).
/// </summary>
public sealed record MessageHistorySlice(IReadOnlyList<Message> Items, bool HasMore);
