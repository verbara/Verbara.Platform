using Asterisk.Platform.Channels.Video;

namespace Asterisk.Platform.Channels.Video.Tests;

public class VideoSessionTests
{
    [Fact]
    public void VideoSession_ShouldStoreAllProperties_WhenCreated()
    {
        var sessionId = "session-xyz";
        var signalingUrl = "wss://signaling.example.com/session-xyz";
        var customerToken = "cust-tok-abc";
        var agentToken = "agent-tok-def";
        var createdAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var session = new VideoSession(sessionId, signalingUrl, customerToken, agentToken, createdAt);

        session.SessionId.Should().Be(sessionId);
        session.SignalingUrl.Should().Be(signalingUrl);
        session.CustomerToken.Should().Be(customerToken);
        session.AgentToken.Should().Be(agentToken);
        session.CreatedAt.Should().Be(createdAt);
    }

    [Fact]
    public void VideoSession_ShouldBeEqual_WhenSameValues()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var session1 = new VideoSession("s1", "wss://url", "cust", "agent", createdAt);
        var session2 = new VideoSession("s1", "wss://url", "cust", "agent", createdAt);

        session1.Should().Be(session2);
    }
}
