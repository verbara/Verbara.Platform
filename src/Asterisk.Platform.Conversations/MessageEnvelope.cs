namespace Asterisk.Platform.Conversations;

public sealed record MessageEnvelope(IReadOnlyList<MessageBlock> Blocks);
