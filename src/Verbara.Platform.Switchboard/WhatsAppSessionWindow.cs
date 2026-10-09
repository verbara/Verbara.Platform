using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Switchboard;

/// <summary>How an outbound WhatsApp message may go out (whatsapp-outbound, design D7).</summary>
public enum WhatsAppSendMode
{
    /// <summary>The customer-service window is open and no template was named: send free-form.</summary>
    FreeForm,

    /// <summary>The caller named a template: send it, whether or not the window is open.</summary>
    Template,

    /// <summary>The window is closed and no template was named: refuse, nothing is sent.</summary>
    TemplateRequired,
}

/// <summary>
/// The typed outcome of the 24-hour window check. A refusal carries <see cref="TemplateRequiredCode"/>, which the
/// API surfaces as <c>409 whatsapp-template-required</c>.
/// </summary>
public sealed record WhatsAppWindowDecision(WhatsAppSendMode Mode, DateTimeOffset? LastInboundAt)
{
    /// <summary>Refusal code for a free-form send outside the window.</summary>
    public const string TemplateRequiredCode = "whatsapp-template-required";

    /// <summary>True when the send must be refused.</summary>
    public bool IsRefused => Mode == WhatsAppSendMode.TemplateRequired;

    /// <summary><see cref="TemplateRequiredCode"/> when refused, otherwise <c>null</c>.</summary>
    public string? RefusalCode => IsRefused ? TemplateRequiredCode : null;
}

/// <summary>Decides whether a WhatsApp send needs a template, from the conversation's stored inbound history.</summary>
public interface IWhatsAppSessionWindow
{
    Task<WhatsAppWindowDecision> DecideAsync(
        TenantId tenantId, EntityId conversationId, string? templateId, CancellationToken ct);
}

/// <summary>
/// WhatsApp's customer-service window, read from the message store (design D7): a free-form message is allowed
/// for 24 hours after the conversation's most recent inbound message. Because it is derived from stored messages
/// it survives restarts and is the same on every replica — unlike the per-instance in-memory tracker it replaces.
/// A template goes out only when the caller names one.
/// </summary>
public sealed class WhatsAppSessionWindow : IWhatsAppSessionWindow
{
    /// <summary>Length of the customer-service window.</summary>
    public static readonly TimeSpan Length = TimeSpan.FromHours(24);

    private readonly IMessageStore _messageStore;
    private readonly IClock _clock;

    public WhatsAppSessionWindow(IMessageStore messageStore, IClock clock)
    {
        _messageStore = messageStore;
        _clock = clock;
    }

    public async Task<WhatsAppWindowDecision> DecideAsync(
        TenantId tenantId, EntityId conversationId, string? templateId, CancellationToken ct)
    {
        var lastInbound = await _messageStore.FindLastInboundAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        return Decide(lastInbound?.CreatedAt, _clock.UtcNow, templateId);
    }

    /// <summary>
    /// Pure rule: a named template always goes out as a template; otherwise free-form while
    /// <c>now - lastInboundAt &lt; 24h</c>, and refused once 24 hours have passed or when there is no inbound message.
    /// </summary>
    public static WhatsAppWindowDecision Decide(DateTimeOffset? lastInboundAt, DateTimeOffset now, string? templateId)
    {
        if (!string.IsNullOrWhiteSpace(templateId))
            return new WhatsAppWindowDecision(WhatsAppSendMode.Template, lastInboundAt);

        var open = lastInboundAt is { } last && now - last < Length;
        return new WhatsAppWindowDecision(open ? WhatsAppSendMode.FreeForm : WhatsAppSendMode.TemplateRequired, lastInboundAt);
    }
}
