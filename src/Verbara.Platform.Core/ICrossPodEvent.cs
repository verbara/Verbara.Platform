namespace Verbara.Platform.Core;

/// <summary>
/// Marks a <see cref="PlatformEvent"/> that is distributed CROSS-POD via the Redis push
/// backplane (not only SSE). A cross-pod event MUST also be registered in
/// <c>Verbara.Platform.Core.Push.PlatformPushJsonContext</c> (backplane payload) and handled by
/// <c>Verbara.Platform.Realtime.Services.RemoteEventDispatcher</c> (cross-node decode/republish).
/// The guard tests enforce this by enumerating <see cref="ICrossPodEvent"/> implementers — a
/// compile-time contract; no runtime reflection ships.
/// </summary>
public interface ICrossPodEvent;
