using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Video;

public interface IVideoTransport
{
    Task<VideoSession> CreateSessionAsync(VideoSessionRequest request, CancellationToken ct);
    Task EndSessionAsync(string sessionId, CancellationToken ct);
    Task<VideoSessionStatus> GetStatusAsync(string sessionId, CancellationToken ct);
}

public sealed record VideoSessionRequest(
    TenantId TenantId,
    EntityId ConversationId,
    EntityId AgentId,
    string CustomerIdentifier);

public sealed record VideoSession(
    string SessionId,
    string SignalingUrl,
    string CustomerToken,
    string AgentToken,
    DateTimeOffset CreatedAt);

public enum VideoSessionStatus { Waiting, Connected, Ended }
