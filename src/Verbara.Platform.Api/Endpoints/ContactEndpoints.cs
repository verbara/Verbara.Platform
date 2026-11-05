using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Typification.Stores;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class ContactEndpoints
{
    /// <summary>A contact write named a WebChat identity key, which only <c>POST /webchat/sessions</c> writes (design D12).</summary>
    internal const string ReservedContactAddressCode = "reserved-contact-address";

    /// <summary>
    /// 403: the caller's roles do not hold the permission of the contact write (<c>contacts:contact:create</c>,
    /// <c>:edit</c>, <c>:delete</c>) or of the opt-out (<c>contacts:conversation:handle</c>). Evaluated first, so the
    /// answer says nothing about the contact or the body. The code the supervisor listen routes already answer.
    /// </summary>
    internal const string NotPermittedCode = SupervisorListenRefusal.NotPermitted;

    /// <summary>
    /// 400: a <c>channelConsent</c> key of an update, or a <c>channels</c> value of an opt-out, is not a channel name as
    /// <c>GET</c> writes it (compared ordinally); <c>details</c> lists each such value once. Nothing is written.
    /// </summary>
    internal const string UnknownConsentChannelCode = "unknown-consent-channel";

    private const string ReservedAddressDescription =
        "A WebChat address that starts with `webchat-visitor:` or `webchat-external:` answers 400 `reserved-contact-address`, " +
        "with the offending addresses in `details`, and nothing is written. `identityLevel` in the body is ignored.";

    public static void MapContactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/contacts").RequireAuthorization("Authenticated").RequireOperationalTenant();

        group.MapGet("/{id}", GetContact);
        // contact-history-opens-by-assignment (design D5, D13, D14): governed by ConversationReadRule; an unknown contact
        // answers an empty 404 before the rule runs, a refusal is 403 not-readable.
        group.MapGet("/{id}/conversations", GetContactConversations)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        group.MapGet("/", SearchContacts);
        group.MapPost("/", CreateContact)
            .WithDescription(ReservedAddressDescription);
        group.MapPut("/{id}", UpdateContact)
            .WithDescription(ReservedAddressDescription);
        group.MapDelete("/{id}", DeleteContact);
    }

    private static async Task<Results<Ok<Contact>, NotFound>> GetContact(
        string id,
        HttpContext context,
        [FromServices] IContactStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var contact = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return contact is null ? TypedResults.NotFound() : TypedResults.Ok(contact);
    }

    /// <summary>The largest page of a contact's history (design D5).</summary>
    internal const int MaxContactConversationsPageSize = 100;

    /// <summary>
    /// The contact's conversations, one summary each, newest first, every state included (design D5, D6, D9): 404 for an
    /// unknown contact, then the read rule (<see cref="ConversationReader.CanReadContactAsync"/>), then one summary query,
    /// restricted to the caller's own conversations for a shared contact (D18). <paramref name="pageSize"/> is clamped to
    /// 1..100 and <paramref name="page"/> to at least 1.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<ContactConversationSummaryDto>>, NotFound, JsonHttpResult<ErrorResponse>>> GetContactConversations(
        string id,
        HttpContext context,
        [FromServices] IConversationStore store,
        [FromServices] IContactStore contacts,
        [FromServices] IAgentStore agents,
        [FromServices] ITypificationSchemaStore schemas,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var contact = await contacts.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (contact is null)
            return TypedResults.NotFound();

        var reader = await ConversationReadRule.ResolveAsync(context, tenantId, agents, store, contacts, ct);
        if (!await reader.CanReadContactAsync(contact, ct))
            return ConversationReadRule.Refusal();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxContactConversationsPageSize);
        var summaries = await store.ListContactSummariesAsync(
            tenantId, contact.ContactId, reader.OwnerRestrictionOf(contact), page, pageSize, ct);

        var labels = await DispositionLabelsAsync(tenantId, summaries.Items, schemas, ct);
        var items = summaries.Items
            .Select(summary => ContactConversationSummaryMapper.ToDto(
                summary,
                reader.IsHeldByCaller(summary.Conversation),
                summary.Disposition is { } disposition ? labels.GetValueOrDefault(disposition) : null))
            .ToList();
        return TypedResults.Ok(new PagedResult<ContactConversationSummaryDto>(items, summaries.TotalCount, page, pageSize));
    }

    /// <summary>
    /// The label of each disposition of the page, read from the schema version it was submitted against (design D6):
    /// one schema read per distinct (schema, version) pair, usually one. A missing schema or node has no entry, so the
    /// summary keeps <c>nodeId</c> and omits <c>label</c>.
    /// </summary>
    private static async Task<Dictionary<ConversationDispositionRef, string>> DispositionLabelsAsync(
        TenantId tenantId,
        IReadOnlyList<ContactConversationSummary> summaries,
        ITypificationSchemaStore schemas,
        CancellationToken ct)
    {
        var labels = new Dictionary<ConversationDispositionRef, string>();
        var dispositions = summaries.Select(s => s.Disposition).OfType<ConversationDispositionRef>().ToList();
        foreach (var version in dispositions.Select(d => (d.SchemaId, d.SchemaVersion)).Distinct())
        {
            var schema = await schemas.GetByIdAsync(tenantId, version.SchemaId, version.SchemaVersion, ct);
            if (schema is null)
                continue;

            var resolved = dispositions
                .Where(d => d.SchemaId == version.SchemaId && d.SchemaVersion == version.SchemaVersion)
                .Select(d => (Disposition: d, Node: schema.Nodes.FirstOrDefault(node => node.NodeId == d.NodeId)))
                .Where(pair => pair.Node is not null);
            foreach (var (disposition, node) in resolved)
                labels[disposition] = node!.Label;
        }

        return labels;
    }

    private static async Task<Ok<PagedResult<Contact>>> SearchContacts(
        HttpContext context,
        [FromServices] IContactStore store,
        string? search = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var result = await store.SearchAsync(tenantId, search, new PagedQuery(page, pageSize), ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Created<Contact>, BadRequest<ErrorDetailResponse>>> CreateContact(
        HttpContext context,
        [FromBody] CreateContactRequest body,
        [FromServices] IContactStore store,
        IClock clock,
        CancellationToken ct)
    {
        var reserved = ReservedAddresses(body.Addresses);
        if (reserved.Count > 0)
            return TypedResults.BadRequest(new ErrorDetailResponse(ReservedContactAddressCode, reserved));

        var tenantId = GetTenantId(context);
        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tenantId,
            FirstName = body.FirstName,
            LastName = body.LastName,
            Company = body.Company,
            Segment = body.Segment,
            PreferredChannel = body.PreferredChannel,
            PreferredLanguage = body.PreferredLanguage,
            Timezone = body.Timezone,
            CreatedAt = clock.UtcNow,
        };

        if (body.Addresses is { } addresses)
        {
            foreach (var addr in addresses)
                contact.AddAddress(new ChannelAddress(addr.Channel, addr.Address));
        }

        if (body.CustomFields is { } fields)
        {
            foreach (var (key, value) in fields)
                contact.SetCustomField(key, value);
        }

        await store.SaveAsync(contact, ct);
        return TypedResults.Created($"/contacts/{contact.ContactId}", contact);
    }

    private static async Task<Results<Ok<Contact>, NotFound, BadRequest<ErrorDetailResponse>>> UpdateContact(
        string id,
        HttpContext context,
        [FromBody] UpdateContactRequest body,
        [FromServices] IContactStore store,
        IClock clock,
        CancellationToken ct)
    {
        var reserved = ReservedAddresses(body.Addresses);
        if (reserved.Count > 0)
            return TypedResults.BadRequest(new ErrorDetailResponse(ReservedContactAddressCode, reserved));

        var tenantId = GetTenantId(context);
        var contact = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (contact is null)
            return TypedResults.NotFound();

        if (body.FirstName is not null) contact.FirstName = body.FirstName;
        if (body.LastName is not null) contact.LastName = body.LastName;
        if (body.Company is not null) contact.Company = body.Company;
        if (body.Segment is not null) contact.Segment = body.Segment;
        if (body.PreferredChannel.HasValue) contact.PreferredChannel = body.PreferredChannel.Value;
        if (body.PreferredLanguage is not null) contact.PreferredLanguage = body.PreferredLanguage;
        if (body.Timezone is not null) contact.Timezone = body.Timezone;
        if (body.DoNotContact.HasValue) contact.DoNotContact = body.DoNotContact.Value;

        if (body.Addresses is { } addresses)
        {
            foreach (var addr in addresses)
                contact.AddAddress(new ChannelAddress(addr.Channel, addr.Address));
        }

        if (body.CustomFields is { } fields)
        {
            foreach (var (key, value) in fields)
                contact.SetCustomField(key, value);
        }

        contact.UpdatedAt = clock.UtcNow;
        await store.SaveAsync(contact, ct);
        return TypedResults.Ok(contact);
    }

    private static async Task<IResult> DeleteContact(
        string id,
        HttpContext context,
        [FromServices] IContactStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.DeleteAsync(tenantId, EntityId.From(id), ct);
        return Results.NoContent();
    }

    /// <summary>
    /// The WebChat identity keys a write body names, each once and verbatim. A blank address is not one of them: it goes on
    /// to the <see cref="ChannelAddress"/> constructor, as any other blank address does.
    /// </summary>
    private static List<string> ReservedAddresses(IReadOnlyList<ChannelAddressDto>? addresses) =>
        addresses is null
            ? []
            :
            [
                .. addresses
                    .Where(a => !string.IsNullOrWhiteSpace(a.Address) && new ChannelAddress(a.Channel, a.Address).IsWebChatIdentityKey())
                    .Select(a => a.Address)
                    .Distinct(StringComparer.Ordinal),
            ];

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

internal sealed record CreateContactRequest(
    string? FirstName = null,
    string? LastName = null,
    string? Company = null,
    string? Segment = null,
    ChannelType? PreferredChannel = null,
    string? PreferredLanguage = null,
    string? Timezone = null,
    IReadOnlyList<ChannelAddressDto>? Addresses = null,
    IReadOnlyDictionary<string, string>? CustomFields = null);

internal sealed record UpdateContactRequest(
    string? FirstName = null,
    string? LastName = null,
    string? Company = null,
    string? Segment = null,
    ChannelType? PreferredChannel = null,
    string? PreferredLanguage = null,
    string? Timezone = null,
    bool? DoNotContact = null,
    IReadOnlyList<ChannelAddressDto>? Addresses = null,
    IReadOnlyDictionary<string, string>? CustomFields = null,
    IReadOnlyDictionary<string, bool>? ChannelConsent = null);

internal sealed record ChannelAddressDto(ChannelType Channel, string Address);

/// <summary>
/// The body of <c>POST /contacts/{id}/opt-out</c>: <see cref="DoNotContact"/> <see langword="true"/> sets the contact's
/// do-not-contact, and each of <see cref="Channels"/> (channel names as <c>GET</c> writes them) records <c>false</c> for
/// that channel. Both members are optional; the route never clears do-not-contact and never records <c>true</c>.
/// </summary>
internal sealed record ContactOptOutRequest(bool? DoNotContact = null, IReadOnlyList<string>? Channels = null);

/// <summary>
/// The before or after of a contact's do-not-contact and consent in a contact audit record (design D17): those two
/// members only, never a name or an address. Serialized to JSON through <c>ApiJsonContext</c> before it reaches the
/// audit store, whose own serializer has no context for it.
/// </summary>
internal sealed record ContactConsentAuditState(bool DoNotContact, IReadOnlyDictionary<string, bool> ChannelConsent);
