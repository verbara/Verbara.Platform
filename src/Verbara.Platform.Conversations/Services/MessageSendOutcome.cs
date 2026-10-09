namespace Verbara.Platform.Conversations.Services;

/// <summary>
/// The typed outcome of <see cref="IConversationService.TrySendMessageAsync"/>: either the stored message (sent,
/// or failed by the provider), or a refusal whose stable <see cref="RefusalCode"/> the API maps to a status —
/// for example <c>whatsapp-template-required</c> → 409. A refused send stores nothing and reaches no provider.
/// </summary>
public sealed record MessageSendOutcome
{
    private MessageSendOutcome(Message? message, string? refusalCode)
    {
        Message = message;
        RefusalCode = refusalCode;
    }

    /// <summary>The stored message; <c>null</c> when the send was refused.</summary>
    public Message? Message { get; }

    /// <summary>The machine code of the refusal; <c>null</c> when a message was stored.</summary>
    public string? RefusalCode { get; }

    /// <summary>True when the send was refused before anything was stored or sent.</summary>
    public bool IsRefused => RefusalCode is not null;

    /// <summary>A stored message.</summary>
    public static MessageSendOutcome Stored(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new MessageSendOutcome(message, null);
    }

    /// <summary>A refusal with the stable <paramref name="code"/>.</summary>
    public static MessageSendOutcome Refused(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new MessageSendOutcome(null, code);
    }
}
