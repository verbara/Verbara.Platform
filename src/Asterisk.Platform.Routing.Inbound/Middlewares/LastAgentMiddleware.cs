using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Routing.Inbound.Middlewares;

public sealed class LastAgentMiddleware : InboundRoutingMiddlewareBase
{
    private readonly IConversationStore _conversationStore;
    private readonly InboundRoutingOptions _options;

    public LastAgentMiddleware(IConversationStore conversationStore, IOptions<InboundRoutingOptions> options)
    {
        _conversationStore = conversationStore;
        _options = options.Value;
    }

    public override async Task<RouteResult?> RouteAsync(
        RoutingContext context,
        Func<Task<RouteResult?>> continuation,
        CancellationToken ct)
    {
        EntityId? preferredAgentId = null;

        if (_options.EnableLastAgentRouting)
        {
            var query = new ConversationQuery
            {
                ContactId = context.Contact.ContactId,
                PageSize = 10,
            };

            var history = await _conversationStore.ListAsync(context.TenantId, query, ct).ConfigureAwait(false);
            var window = DateTimeOffset.UtcNow - _options.LastAgentWindow;

            foreach (var conv in history.Items)
            {
                if (conv.ClosedAt.HasValue && conv.ClosedAt.Value >= window
                    && conv.Owner?.Kind == ConversationOwnerKind.Agent
                    && conv.Owner.OwnerId.HasValue)
                {
                    preferredAgentId = conv.Owner.OwnerId.Value;
                    break;
                }
            }
        }

        var result = await continuation().ConfigureAwait(false);

        if (result is not null && preferredAgentId.HasValue)
            return result with { PreferredAgentId = preferredAgentId };

        return result;
    }
}
