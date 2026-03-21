using Asterisk.Platform.Conversations.Services;

namespace Asterisk.Platform.Channels.Core.Pipeline;

public sealed class ContactResolutionStep : IPipelineStep
{
    private readonly IContactIdentityResolver _resolver;

    public ContactResolutionStep(IContactIdentityResolver resolver)
    {
        _resolver = resolver;
    }

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        context.Contact = await _resolver.ResolveAsync(
            context.TenantId,
            context.InboundMessage.From,
            ct);

        return context;
    }
}
