using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Core;

public sealed class ChannelRegistry : IChannelRegistry
{
    private readonly Dictionary<ChannelType, IChannelConnector> _connectors = new();
    private readonly Dictionary<ChannelType, IWebhookHandler> _handlers = new();
    private readonly Dictionary<ChannelType, ChannelConstraints> _constraints = new();

    public IReadOnlyList<ChannelType> AvailableChannels => [.. _connectors.Keys];

    public void RegisterConnector(IChannelConnector connector)
    {
        _connectors[connector.Channel] = connector;
    }

    public void RegisterHandler(IWebhookHandler handler)
    {
        _handlers[handler.Channel] = handler;
    }

    public void RegisterConstraints(ChannelType channel, ChannelConstraints constraints)
    {
        _constraints[channel] = constraints;
    }

    public IChannelConnector GetConnector(ChannelType channel)
    {
        if (!_connectors.TryGetValue(channel, out var connector))
            throw new InvalidOperationException($"No connector registered for channel '{channel}'.");
        return connector;
    }

    public IWebhookHandler GetHandler(ChannelType channel)
    {
        if (!_handlers.TryGetValue(channel, out var handler))
            throw new InvalidOperationException($"No webhook handler registered for channel '{channel}'.");
        return handler;
    }

    public ChannelConstraints GetConstraints(ChannelType channel)
    {
        if (!_constraints.TryGetValue(channel, out var constraints))
            throw new InvalidOperationException($"No constraints registered for channel '{channel}'.");
        return constraints;
    }
}
