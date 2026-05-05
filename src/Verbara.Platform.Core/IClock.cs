namespace Verbara.Platform.Core;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
