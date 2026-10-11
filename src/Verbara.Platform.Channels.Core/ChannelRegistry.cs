using System.Diagnostics.CodeAnalysis;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

/// <summary>
/// The channels the host serves, composed from the <see cref="IWebhookHandler"/> and
/// <see cref="IChannelConnector"/> registrations that channel modules add to DI
/// (whatsapp-works-for-real D1). Registering a module is the only act that exposes its channel; nothing
/// registers a channel imperatively. The composition happens on first use, never at host start, so a
/// boot without channel configuration (design-time OpenAPI export, headless tools) resolves no connector.
/// </summary>
public sealed class ChannelRegistry : IChannelRegistry
{
    private readonly Lazy<Composition> _composition;
    private readonly Dictionary<ChannelType, ChannelConstraints> _constraints = new();

    /// <summary>Creates a registry over the given handlers and connectors.</summary>
    public ChannelRegistry(IEnumerable<IWebhookHandler> handlers, IEnumerable<IChannelConnector> connectors)
        : this(() => handlers, () => connectors)
    {
    }

    /// <summary>
    /// Creates a registry whose handlers and connectors are enumerated on first use (the DI path:
    /// <c>AddPlatformChannels</c> passes the service provider's enumerations).
    /// </summary>
    public ChannelRegistry(
        Func<IEnumerable<IWebhookHandler>> handlers,
        Func<IEnumerable<IChannelConnector>> connectors)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(connectors);
        _composition = new Lazy<Composition>(
            () => Composition.Build(handlers(), connectors()),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<ChannelType> AvailableChannels => [.. _composition.Value.Connectors.Keys];

    public IReadOnlyList<ChannelType> WebhookChannels => [.. _composition.Value.Handlers.Keys];

    public void RegisterConstraints(ChannelType channel, ChannelConstraints constraints)
    {
        _constraints[channel] = constraints;
    }

    public IChannelConnector GetConnector(ChannelType channel)
    {
        if (!_composition.Value.Connectors.TryGetValue(channel, out var connector))
            throw new InvalidOperationException($"No connector registered for channel '{channel}'.");
        return connector;
    }

    public IWebhookHandler GetHandler(ChannelType channel)
    {
        if (!TryGetHandler(channel, out var handler))
            throw new InvalidOperationException($"No webhook handler registered for channel '{channel}'.");
        return handler;
    }

    public bool TryGetHandler(ChannelType channel, [NotNullWhen(true)] out IWebhookHandler? handler) =>
        _composition.Value.Handlers.TryGetValue(channel, out handler);

    public ChannelConstraints GetConstraints(ChannelType channel)
    {
        if (!_constraints.TryGetValue(channel, out var constraints))
            throw new InvalidOperationException($"No constraints registered for channel '{channel}'.");
        return constraints;
    }

    private sealed record Composition(
        IReadOnlyDictionary<ChannelType, IWebhookHandler> Handlers,
        IReadOnlyDictionary<ChannelType, IChannelConnector> Connectors)
    {
        public static Composition Build(IEnumerable<IWebhookHandler> handlers, IEnumerable<IChannelConnector> connectors)
        {
            var byChannelHandlers = new Dictionary<ChannelType, IWebhookHandler>();
            foreach (var handler in handlers)
            {
                if (!byChannelHandlers.TryAdd(handler.Channel, handler))
                    throw new InvalidOperationException(
                        $"More than one webhook handler is registered for channel '{handler.Channel}'.");
            }

            var byChannelConnectors = new Dictionary<ChannelType, IChannelConnector>();
            foreach (var connector in connectors)
            {
                if (!byChannelConnectors.TryAdd(connector.Channel, connector))
                    throw new InvalidOperationException(
                        $"More than one connector is registered for channel '{connector.Channel}'.");
            }

            return new Composition(byChannelHandlers, byChannelConnectors);
        }
    }
}
