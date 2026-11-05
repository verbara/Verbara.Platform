using Verbara.Platform.Conversations.Services;

namespace Verbara.Platform.Channels.Core.Pipeline;

public sealed class ContactResolutionStep : IPipelineStep
{
    private readonly IContactIdentityResolver _resolver;

    public ContactResolutionStep(IContactIdentityResolver resolver)
    {
        _resolver = resolver;
    }

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        var inbound = context.InboundMessage;

        // A session's message belongs to the session's contact whatever that contact's addresses say now, and writes
        // nothing to it. A contact erased since falls through: the sender becomes a new contact holding this address.
        if (inbound.SessionBinding is { } binding
            && await _resolver.FindByIdAsync(context.TenantId, binding.ContactId, ct).ConfigureAwait(false) is { } bound)
        {
            context.Contact = bound;
            return context;
        }

        context.Contact = await _resolver.ResolveAsync(
            context.TenantId,
            new ContactIdentity(inbound.From, inbound.AlternateAddresses, inbound.ProfileName),
            ct);

        return context;
    }
}
