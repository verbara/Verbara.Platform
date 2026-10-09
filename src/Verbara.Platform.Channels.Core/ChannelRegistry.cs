using Verbara.Platform.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Channels.Core;

/// <summary>
/// Channel lookup for outbound sends and inbound webhooks. When built by DI, the connectors are the
/// <see cref="IChannelConnector"/> services the channel modules registered (their <c>AddXxx</c>
/// forwards the connector), resolved on first use — never at host start, so a boot without channel
/// configuration resolves nothing. An explicit <see cref="RegisterConnector"/> wins over a DI one.
/// </summary>
public sealed class ChannelRegistry : IChannelRegistry
{
    private readonly Dictionary<ChannelType, IChannelConnector> _connectors = new();
    private readonly Dictionary<ChannelType, IWebhookHandler> _handlers = new();
    private readonly Dictionary<ChannelType, ChannelConstraints> _constraints = new();
    private readonly Lazy<bool> _diConnectorsComposed;

    public ChannelRegistry()
    {
        _diConnectorsComposed = new Lazy<bool>(() => true);
    }

    public ChannelRegistry(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _diConnectorsComposed = new Lazy<bool>(() =>
        {
            foreach (var connector in services.GetServices<IChannelConnector>())
                _connectors.TryAdd(connector.Channel, connector);
            return true;
        });
    }

    public IReadOnlyList<ChannelType> AvailableChannels
    {
        get
        {
            EnsureDiConnectors();
            return [.. _connectors.Keys];
        }
    }

    public void RegisterConnector(IChannelConnector connector)
    {
        EnsureDiConnectors();
        _connectors[connector.Channel] = connector;
    }

    private void EnsureDiConnectors() => _ = _diConnectorsComposed.Value;

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
        EnsureDiConnectors();
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
