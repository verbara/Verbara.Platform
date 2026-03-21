namespace Asterisk.Platform.Channels.Video;

public sealed class VideoOptions
{
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(60);
    public bool EnableScreenSharing { get; set; }
    public bool EnableRecording { get; set; }
    public int MaxParticipants { get; set; } = 2;
    public VideoQuality DefaultQuality { get; set; } = VideoQuality.Medium;
}

public enum VideoQuality { Low, Medium, High }
