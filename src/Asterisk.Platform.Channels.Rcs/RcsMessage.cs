namespace Asterisk.Platform.Channels.Rcs;

public abstract record RcsMessage;

public sealed record RcsTextMessage(string Text) : RcsMessage;

public sealed record RcsRichCardMessage(
    string Title,
    string Description,
    string? ImageUrl,
    IReadOnlyList<RcsSuggestion> Suggestions) : RcsMessage;

public sealed record RcsCarouselMessage(IReadOnlyList<RcsRichCardMessage> Cards) : RcsMessage;

public abstract record RcsSuggestion(string Text);

public sealed record RcsReplySuggestion(string Text, string PostbackData) : RcsSuggestion(Text);

public sealed record RcsUrlSuggestion(string Text, string Url) : RcsSuggestion(Text);

public sealed record RcsDialSuggestion(string Text, string PhoneNumber) : RcsSuggestion(Text);
