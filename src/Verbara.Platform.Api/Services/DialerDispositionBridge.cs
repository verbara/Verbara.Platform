using System.Globalization;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Conversations;
using Verbara.Platform.Typification;
using Verbara.Sdk.Pro.Dialer.Dispositions;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Resolves what a voice typification owes the dialer, before it is written
/// (dialer-honours-dnc-callbacks-and-agent-dispositions, design D4, D5, D16): the Conversation must carry the
/// campaign id and the dialer's action id (stamped by <see cref="VoiceConversationBridge"/>), and the leaf's dialer
/// code must match an active disposition code of that campaign. A leaf with no dialer code, or a Conversation that
/// is not a campaign call, owes nothing; a code the campaign does not have owes nothing and is logged.
/// </summary>
internal static partial class DialerDispositionBridge
{
    private const string CampaignIdKey = "campaignId";

    /// <returns>The pre-claimed dispatch row to store with the submission, or <see langword="null"/> when none is owed.</returns>
    public static async Task<DialerDispositionDispatch?> ResolveAsync(
        Conversation conversation,
        LeafOutcome? leaf,
        TypifyRequest body,
        string agentId,
        DispositionCodeStoreBase dispositionCodes,
        DialerDispositionDispatcher? dispatcher,
        ILogger logger,
        CancellationToken ct)
    {
        if (leaf?.DialerCode is not { Length: > 0 } dialerCode)
            return null;

        var metadata = conversation.Metadata;
        if (!metadata.TryGetValue(CampaignIdKey, out var campaignText)
            || !long.TryParse(campaignText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var campaignId)
            || !metadata.TryGetValue(VoiceConversationBridge.DialerActionIdKey, out var actionId)
            || string.IsNullOrWhiteSpace(actionId))
            return null;

        var tenantId = conversation.TenantId.Value;
        var codes = await dispositionCodes.ListByCampaignAsync(tenantId, campaignId, ct).ConfigureAwait(false);
        var code = codes.FirstOrDefault(c => c.IsActive && string.Equals(c.Code, dialerCode, StringComparison.Ordinal));
        if (code is null)
        {
            LogNoMatchingCode(logger, campaignId, dialerCode, conversation.ConversationId.Value);
            return null;
        }

        if (dispatcher is null)
        {
            // The dialer's stores answered, so the dialer is configured; without its dispatcher nothing can hand over.
            LogNoDispatcher(logger, conversation.ConversationId.Value);
            return null;
        }

        long? dialerContactId =
            metadata.TryGetValue(VoiceConversationBridge.DialerContactIdKey, out var contactText)
            && long.TryParse(contactText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contactId)
                ? contactId
                : null;

        // I1/M4 — the raw body values: the notes are the human-authored wrap-up note, and callback_date is a date
        // field, never free text, so neither is PII-screened.
        DateTimeOffset? callbackAt =
            body.FieldValues.TryGetValue(TypificationFieldKeys.CallbackDate, out var callbackText)
            && DateTimeOffset.TryParse(callbackText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : null;

        return dispatcher.NewPreClaimed(new DialerDispositionDispatch
        {
            TenantId = tenantId,
            ConversationId = conversation.ConversationId.Value,
            CampaignId = campaignId,
            ActionId = actionId,
            DialerContactId = dialerContactId,
            DispositionCodeId = code.Id,
            DispositionCode = code.Code,
            Comment = body.Notes,
            CallbackAt = callbackAt,
            RequestsCallback = leaf.TriggerCallback,
            AgentId = agentId,
        });
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[DIALER-DISPOSITION] Campaign {CampaignId} has no active disposition code {Code}; the typification of Conversation {ConversationId} does not reach the dialer.")]
    private static partial void LogNoMatchingCode(ILogger logger, long campaignId, string code, string conversationId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[DIALER-DISPOSITION] No dialer disposition dispatcher is registered; the typification of Conversation {ConversationId} does not reach the dialer.")]
    private static partial void LogNoDispatcher(ILogger logger, string conversationId);
}
