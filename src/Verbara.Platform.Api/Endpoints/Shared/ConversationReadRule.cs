using Microsoft.AspNetCore.Http.HttpResults;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// The one rule that decides who reads a conversation, its transcript and a contact's list of conversations
/// (contact-history-opens-by-assignment, design D1, D2, D3, D18; Platform/ADR-0047). Built the way
/// <see cref="ConversationActor"/> is: a static factory over the stores the handler already receives, so it needs no
/// DI registration.
/// </summary>
internal static class ConversationReadRule
{
    /// <summary>The one refusal code of every read this rule governs; the causes are not distinguished on the wire.</summary>
    public const string NotReadable = "not-readable";

    /// <summary>403 <c>{"error":"not-readable"}</c>.</summary>
    public static JsonHttpResult<ErrorResponse> Refusal() => ConversationActor.Forbidden(NotReadable);

    /// <summary>Resolves the caller of <paramref name="context"/> as a reader of <paramref name="tenantId"/>.</summary>
    public static async Task<ConversationReader> ResolveAsync(
        HttpContext context,
        TenantId tenantId,
        IAgentStore agents,
        IConversationStore conversations,
        IContactStore contacts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(contacts);
        var readerClass = await ConversationReaderClass.ResolveAsync(context, tenantId, agents, ct);
        return new ConversationReader(readerClass, tenantId, conversations, contacts);
    }
}

/// <summary>A resolved caller of <see cref="ConversationReadRule"/>: what it may read in its tenant.</summary>
internal sealed class ConversationReader
{
    private readonly TenantId _tenantId;
    private readonly IConversationStore _conversations;
    private readonly IContactStore _contacts;

    internal ConversationReader(
        ConversationReaderClass readerClass, TenantId tenantId, IConversationStore conversations, IContactStore contacts)
    {
        Class = readerClass;
        _tenantId = tenantId;
        _conversations = conversations;
        _contacts = contacts;
    }

    /// <summary>The caller's classification, shared with the live stream.</summary>
    public ConversationReaderClass Class { get; }

    /// <inheritdoc cref="ConversationReaderClass.ReadsAll"/>
    public bool ReadsAll => Class.ReadsAll;

    /// <inheritdoc cref="ConversationReaderClass.Handles"/>
    public bool Handles => Class.Handles;

    /// <inheritdoc cref="ConversationReaderClass.Agent"/>
    public Agent? Agent => Class.Agent;

    /// <summary>
    /// Whether the caller may read <paramref name="conversation"/> (design D2 steps 2-5). The caller resolved the id in
    /// its own tenant first and answered 404 when it is missing (step 1); a conversation of another tenant is refused
    /// here too, whoever asks.
    /// </summary>
    /// <remarks>
    /// In evaluation order: <see cref="ReadsAll"/> allows; without <see cref="Handles"/> or an Agent profile the read
    /// is refused; the profile's own conversation (any state, Q4) and an open offer to it (that conversation only, Q3)
    /// allow with no store read; only then is the contact loaded (one primary-key read), and a shared contact (D18) or
    /// a contact that cannot be found refuses; last, the agent must hold a non-terminal conversation of the contact
    /// (<see cref="IConversationStore.HoldsOpenConversationOfContactAsync"/>).
    /// </remarks>
    public async Task<bool> CanReadAsync(Conversation conversation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.TenantId != _tenantId)
            return false;
        if (ReadsAll)
            return true;
        if (HandlingAgentId is not { } agentId)
            return false;
        if (Class.Actor.Owns(conversation) || Class.Actor.IsOfferedTo(conversation))
            return true;

        // Fail closed: a contact that cannot be read cannot be shown not to be shared.
        var contact = await _contacts.GetByIdAsync(_tenantId, conversation.ContactId, ct);
        if (contact is null || SharedContactAddresses.IsShared(contact.Addresses))
            return false;

        return await _conversations.HoldsOpenConversationOfContactAsync(_tenantId, contact.ContactId, agentId, ct);
    }

    /// <summary>
    /// Whether the caller may read <paramref name="contact"/>'s list of conversations (design D5 step 2, D18):
    /// <see cref="ReadsAll"/>, or a handling Agent profile that holds a non-terminal conversation of it (owned, not
    /// offered). For a shared contact the same holding is required; the list is then restricted by
    /// <see cref="OwnerRestrictionOf"/>.
    /// </summary>
    public async Task<bool> CanReadContactAsync(Contact contact, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (contact.TenantId != _tenantId)
            return false;
        if (ReadsAll)
            return true;
        if (HandlingAgentId is not { } agentId)
            return false;

        return await _conversations.HoldsOpenConversationOfContactAsync(_tenantId, contact.ContactId, agentId, ct);
    }

    /// <summary>
    /// The owner the contact list of <paramref name="contact"/> is restricted to for this caller: its Agent profile id
    /// when the contact is shared and the caller is not <see cref="ReadsAll"/> (design D5 step 3, D18), else
    /// <see langword="null"/> (every conversation of the contact is listed). Call it only after
    /// <see cref="CanReadContactAsync"/> allowed the caller.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The contact is shared and the caller has no handling Agent profile: <see cref="CanReadContactAsync"/> refuses
    /// it, so no unrestricted answer is ever given to it.
    /// </exception>
    public EntityId? OwnerRestrictionOf(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (ReadsAll || !SharedContactAddresses.IsShared(contact.Addresses))
            return null;

        return HandlingAgentId ?? throw new InvalidOperationException(
            "A shared contact's list is restricted to the caller's own conversations, and this caller handles none: "
            + "CanReadContactAsync refuses it.");
    }

    /// <summary>
    /// Whether the caller holds <paramref name="conversation"/> (design D3, the <c>heldByCaller</c> flag): it is not
    /// terminal and its owner is the caller's Agent profile. An offered row is not held (Q3), and a caller with no
    /// Agent profile holds nothing. Computed from the loaded row, with no store read.
    /// </summary>
    public bool IsHeldByCaller(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return Class.Actor.Owns(conversation) && !ConversationStateMachine.IsTerminal(conversation.State);
    }

    private EntityId? HandlingAgentId => Class.HandlingAgentId;
}
