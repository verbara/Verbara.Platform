namespace Asterisk.Platform.Core;

public sealed record ChannelAddress
{
    public ChannelType Channel { get; }
    public string Address { get; }

    public ChannelAddress(ChannelType channel, string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        Channel = channel;
        Address = address;
    }

    public override string ToString() =>
        $"{Channel.ToString().ToLowerInvariant()}:{Address}";
}
