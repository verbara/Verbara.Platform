using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Typification.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Verbara.Platform.Api.Endpoints;

internal static class ContactEndpoints
{
    /// <summary>A contact write named a WebChat identity key, which only <c>POST /webchat/sessions</c> writes (design D12).</summary>
    internal const string ReservedContactAddressCode = "reserved-contact-address";

    /// <summary>
    /// 403: the caller's roles do not hold the permission of the contact write (<c>contacts:contact:create</c>,
    /// <c>:edit</c>, <c>:delete</c>) or of the opt-out (<c>contacts:conversation:handle</c>). Evaluated first, before the
    /// body is even read, so the answer says nothing about the contact or the body. The code the supervisor listen routes
    /// already answer.
    /// </summary>
    internal const string NotPermittedCode = SupervisorListenRefusal.NotPermitted;

    /// <summary>
    /// 400: a <c>channelConsent</c> key of an update, or a <c>channels</c> value of an opt-out, is not a channel name as
    /// <c>GET</c> writes it (compared ordinally); <c>details</c> lists each such value once. Nothing is written.
    /// </summary>
    internal const string UnknownConsentChannelCode = "unknown-consent-channel";

    /// <summary>The permission <c>POST /contacts</c> requires (design D1).</summary>
    internal const string CreatePermission = "contacts:contact:create";

    /// <summary>The permission <c>PUT /contacts/{id}</c> requires, for every member of the body (design D1, D3).</summary>
    internal const string EditPermission = "contacts:contact:edit";

    /// <summary>The permission <c>DELETE /contacts/{id}</c> requires (design D1, D4).</summary>
    internal const string DeletePermission = "contacts:contact:delete";

    /// <summary>
    /// The permission <c>POST /contacts/{id}/opt-out</c> requires (design D16): whoever handles a contact's
    /// conversations can record that the contact asked not to be contacted.
    /// </summary>
    internal const string OptOutPermission = "contacts:conversation:handle";

    private const string ReservedAddressDescription =
        "A WebChat address that starts with `webchat-visitor:` or `webchat-external:` answers 400 `reserved-contact-address`, " +
        "with the offending addresses in `details`, and nothing is written. `identityLevel` in the body is ignored.";

    private const string ChannelConsentDescription =
        "`channelConsent` keys are channel names as `GET` writes them (`Voice`, `WhatsApp`, ...), case-sensitive; each " +
        "named channel takes its value and the others keep theirs. Any other key answers 400 `unknown-consent-channel`, " +
        "with the offending keys in `details`, and nothing is written.";

    private const string OptOutDescription =
        "Records that the contact asked not to be contacted: `doNotContact: true` marks the contact do-not-contact and " +
        "each of `channels` is recorded as `false` in `channelConsent`; channels not named keep their value. It never " +
        "clears do-not-contact and never records `true` (that is `PUT /contacts/{id}`). Requires " +
        "`contacts:conversation:handle` (403 `not-permitted`, evaluated first). `channels` are channel names as `GET` " +
        "writes them, case-sensitive; any other value answers 400 `unknown-consent-channel` with the offending values in " +
        "`details`, and nothing is written. A body that changes nothing, or no body at all, answers 200 and writes nothing.";

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
        // Each write checks its permission first, in the handler, so a refusal carries a code (design D1, D2). The body is
        // read after that check (ReadBodyAsync), never bound before the handler runs, so `Accepts` declares it.
        group.MapPost("/", CreateContact)
            .Accepts<CreateContactRequest>("application/json")
            .WithDescription(ReservedAddressDescription)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);
        group.MapPut("/{id}", UpdateContact)
            .Accepts<UpdateContactRequest>("application/json")
            .WithDescription(ReservedAddressDescription + " " + ChannelConsentDescription)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);
        group.MapDelete("/{id}", DeleteContact)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);
        // Restrict-only, for whoever handles the contact (design D16); 400 and 404 are declared by the return type.
        group.MapPost("/{id}/opt-out", OptOutContact)
            .Accepts<ContactOptOutRequest>(isOptional: true, "application/json")
            .WithDescription(OptOutDescription)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);
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

    private static async Task<Results<Created<Contact>, BadRequest<ErrorDetailResponse>, JsonHttpResult<ErrorResponse>, StatusCodeHttpResult>> CreateContact(
        HttpContext context,
        [FromServices] IContactStore store,
        IClock clock,
        CancellationToken ct)
    {
        if (!await IsPermittedAsync(context, CreatePermission))
            return NotPermitted();

        var (body, unreadable) = await ReadBodyAsync<CreateContactRequest>(context, optional: false, ct);
        if (body is null)
            return unreadable!;

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

    private static async Task<Results<Ok<Contact>, NotFound, BadRequest<ErrorDetailResponse>, JsonHttpResult<ErrorResponse>, StatusCodeHttpResult>> UpdateContact(
        string id,
        HttpContext context,
        [FromServices] IContactStore store,
        [FromServices] IContactAuditWriter writer,
        IClock clock,
        CancellationToken ct)
    {
        if (!await IsPermittedAsync(context, EditPermission))
            return NotPermitted();

        var (body, unreadable) = await ReadBodyAsync<UpdateContactRequest>(context, optional: false, ct);
        if (body is null)
            return unreadable!;

        var reserved = ReservedAddresses(body.Addresses);
        if (reserved.Count > 0)
            return TypedResults.BadRequest(new ErrorDetailResponse(ReservedContactAddressCode, reserved));

        var unknownChannels = UnknownChannelNames(body.ChannelConsent?.Keys);
        if (unknownChannels.Count > 0)
            return TypedResults.BadRequest(new ErrorDetailResponse(UnknownConsentChannelCode, unknownChannels));

        var tenantId = GetTenantId(context);
        var contact = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (contact is null)
            return TypedResults.NotFound();

        var before = ContactAuditRecord.StateOf(contact);
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

        // Merged: a channel the member names takes its value, every other channel keeps the stored one (design D8).
        if (body.ChannelConsent is { } consent)
        {
            foreach (var (name, consented) in consent)
                contact.SetConsent(ChannelsByName[name], consented);
        }

        var now = clock.UtcNow;
        contact.UpdatedAt = now;

        // A change of do-not-contact or of a consent value is written with its audit record, attributed to the caller,
        // or not at all (design D17). Any other update is a plain save and records nothing.
        var after = ContactAuditRecord.StateOf(contact);
        if (ContactAuditRecord.Same(before, after))
        {
            await store.SaveAsync(contact, ct);
            return TypedResults.Ok(contact);
        }

        var actor = ContactActor.Of(context.User);
        contact.UpdatedBy = actor.ActorId;
        await writer.SaveAsync(
            contact, ContactAuditRecord.Change(tenantId, ContactAuditRecord.ConsentChangedAction, actor, contact.ContactId, before, after, now), ct);
        return TypedResults.Ok(contact);
    }

    private static async Task<Results<NoContent, JsonHttpResult<ErrorResponse>>> DeleteContact(
        string id,
        HttpContext context,
        [FromServices] IContactStore store,
        [FromServices] IContactAuditWriter writer,
        IClock clock,
        CancellationToken ct)
    {
        if (!await IsPermittedAsync(context, DeletePermission))
            return NotPermitted();

        // The delete and its audit record are written together (design D17). An unknown id deletes nothing and records
        // nothing, and still answers 204.
        var tenantId = GetTenantId(context);
        var contact = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (contact is not null)
        {
            var record = ContactAuditRecord.Deleted(tenantId, ContactActor.Of(context.User), contact, clock.UtcNow);
            await writer.DeleteAsync(tenantId, contact.ContactId, record, ct);
        }

        return TypedResults.NoContent();
    }

    /// <summary>
    /// <c>POST /contacts/{id}/opt-out</c> (design D16): the permission first, then the body (none at all is a body that
    /// changes nothing), then the channel names (as D8 checks <c>channelConsent</c> keys), then the contact's existence,
    /// then <see cref="IContactOptOutService"/> with the caller as the actor.
    /// </summary>
    private static async Task<Results<Ok<Contact>, NotFound, BadRequest<ErrorDetailResponse>, JsonHttpResult<ErrorResponse>, StatusCodeHttpResult>> OptOutContact(
        string id,
        HttpContext context,
        [FromServices] IContactOptOutService optOut,
        CancellationToken ct)
    {
        if (!await IsPermittedAsync(context, OptOutPermission))
            return NotPermitted();

        var (read, unreadable) = await ReadBodyAsync<ContactOptOutRequest>(context, optional: true, ct);
        if (unreadable is not null)
            return unreadable;
        var body = read ?? new ContactOptOutRequest();

        var unknownChannels = UnknownChannelNames(body.Channels);
        if (unknownChannels.Count > 0)
            return TypedResults.BadRequest(new ErrorDetailResponse(UnknownConsentChannelCode, unknownChannels));

        List<ChannelType> channels = body.Channels is null ? [] : [.. body.Channels.Select(name => ChannelsByName[name])];
        var contact = await optOut.OptOutAsync(
            GetTenantId(context), EntityId.From(id), body.DoNotContact == true, channels, ContactActor.Of(context.User), ct);
        return contact is null ? TypedResults.NotFound() : TypedResults.Ok(contact);
    }

    /// <summary>
    /// Whether the caller's roles hold <paramref name="permission"/>, through the <c>Permission:&lt;id&gt;</c> policy
    /// (design D2). Called before the body is validated and before the contact is read.
    /// </summary>
    private static async Task<bool> IsPermittedAsync(HttpContext context, string permission)
    {
        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(context.User, "Permission:" + permission)).Succeeded;
    }

    /// <summary>
    /// The JSON body of a contact write, read only after its permission check (design D2), with the type information and
    /// options the framework binds a <c>[FromBody]</c> parameter with (the HTTP JSON options, whose resolver chain starts
    /// with <c>ApiJsonContext</c>), and refused as such a binding refuses it, with no body: 415 when a body is sent with
    /// a content type that is not JSON; 400 when it is not valid JSON or does not fit the type (an unknown enum name, a
    /// value of the wrong type), or when a required body is absent or <c>null</c>; the status of a request the server
    /// cannot read (413 for a body over the size limit). An absent or <c>null</c> optional body is
    /// <c>(null, null)</c>.
    /// </summary>
    private static async Task<(T? Body, StatusCodeHttpResult? Unreadable)> ReadBodyAsync<T>(
        HttpContext context, bool optional, CancellationToken ct)
        where T : class
    {
        var request = context.Request;
        T? body = null;
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        {
            if (!request.HasJsonContentType())
                return (null, TypedResults.StatusCode(StatusCodes.Status415UnsupportedMediaType));

            var options = context.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
            try
            {
                body = await request.ReadFromJsonAsync((JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)), ct);
            }
            catch (JsonException)
            {
                return (null, TypedResults.StatusCode(StatusCodes.Status400BadRequest));
            }
            catch (BadHttpRequestException ex)
            {
                return (null, TypedResults.StatusCode(ex.StatusCode));
            }
        }

        return body is null && !optional
            ? (null, TypedResults.StatusCode(StatusCodes.Status400BadRequest))
            : (body, null);
    }

    /// <summary>403 <c>{"error": "not-permitted"}</c>.</summary>
    private static JsonHttpResult<ErrorResponse> NotPermitted() =>
        TypedResults.Json(new ErrorResponse(NotPermittedCode), ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden);

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

    /// <summary>
    /// Each defined channel by its name exactly as <c>GET</c> writes it (<c>Voice</c>, <c>WhatsApp</c>, ...), compared
    /// ordinally. Built from the enum's names, never through <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/>, which
    /// would also accept other casings, numbers and comma-joined names.
    /// </summary>
    private static readonly FrozenDictionary<string, ChannelType> ChannelsByName =
        Enum.GetValues<ChannelType>().ToFrozenDictionary(channel => channel.ToString(), StringComparer.Ordinal);

    /// <summary>
    /// The values of <paramref name="names"/> that are not a channel name as <c>GET</c> writes it, each once, in the
    /// order given (design D8). Empty when every value is a channel name or there are none.
    /// </summary>
    private static List<string> UnknownChannelNames(IEnumerable<string>? names) =>
        names is null
            ? []
            : [.. names.Where(name => name is null || !ChannelsByName.ContainsKey(name)).Distinct(StringComparer.Ordinal)];

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
