namespace Asterisk.Platform.Core;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
