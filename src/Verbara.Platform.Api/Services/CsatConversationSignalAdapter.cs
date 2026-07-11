using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.CsatRunner.Contracts;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Platform's in-process implementation of the Pro-defined
/// <see cref="ICsatConversationSignal"/> seam (csat-runner Phase E2, task 5b.1; Platform/ADR-0020 +
/// verbara-meta/ADR-0005 open-core boundary). Pro's webchat channel adapter calls
/// <see cref="SendSystemMessageAsync"/> via DI — same process, NOT an API call — to emit a
/// <c>csat_requested</c> system message on the still-open webchat conversation so the Web embed
/// renders the rating panel. Pro depends only on the opaque contract name and the string identity
/// facts; the heavy Platform value types (<see cref="EntityId"/>, <see cref="MessageEnvelope"/>,
/// <see cref="ConversationOwnerKind"/>) stay Platform-side, resolved here.
/// </summary>
/// <remarks>
/// Bridges to <see cref="IConversationService.SendMessageAsync"/> with
/// <see cref="ConversationOwnerKind.System"/> as the sender kind (which the conversation service
/// stamps as <c>MessageDirection.System</c>), a synthetic <c>system</c> sender id, and a single
/// <see cref="TextBlock"/> carrying the system-message type as its payload — the same shape
/// <c>DefaultActionExecutor</c>'s system-message send uses. The gates (license / queue-enabled /
/// sampling) all run upstream in the Pro orchestrator, so this adapter unconditionally dispatches
/// when invoked.
/// </remarks>
internal sealed class CsatConversationSignalAdapter : ICsatConversationSignal
{
    private static readonly EntityId SystemSenderId = EntityId.From("system");

    private readonly IConversationService _conversationService;

    public CsatConversationSignalAdapter(IConversationService conversationService)
    {
        ArgumentNullException.ThrowIfNull(conversationService);
        _conversationService = conversationService;
    }

    public async ValueTask SendSystemMessageAsync(
        string tenantId,
        string conversationId,
        string systemMessageType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentException.ThrowIfNullOrEmpty(systemMessageType);

        var envelope = new MessageEnvelope([new TextBlock(systemMessageType)]);

        await _conversationService.SendMessageAsync(
            EntityId.From(conversationId),
            new TenantId(tenantId),
            envelope,
            SystemSenderId,
            ConversationOwnerKind.System,
            cancellationToken).ConfigureAwait(false);
    }
}
