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
        context.Contact = await _resolver.ResolveAsync(
            context.TenantId,
            new ContactIdentity(inbound.From, inbound.AlternateAddresses, inbound.ProfileName),
            ct);

        return context;
    }
}
