namespace Verbara.Platform.Typification;

/// <summary>
/// Outcome category of a leaf node (the old flat Disposition/DispositionCode
/// semantics collapse here). <c>Retry</c> and <c>SystemResult</c> are reserved
/// for the Pro dialer bridge.
/// </summary>
public enum TypificationCategory
{
    Success = 0,
    Failure = 1,
    FollowUp = 2,
    Retry = 3,
    SystemResult = 4,
}
