namespace Asterisk.Platform.Channels.Core;

public sealed record ChannelConstraints(
    int? MaxMessageLength,
    TimeSpan? SessionWindow,
    bool SupportsRichMedia,
    bool SupportsInteractive,
    bool RequiresTemplateOutsideWindow,
    int? MaxMediaSizeMb);
