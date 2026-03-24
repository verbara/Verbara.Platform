using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ContactEndpoints
{
    public static void MapContactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contacts").RequireAuthorization("Authenticated");

        group.MapGet("/{id}", GetContact);
        group.MapGet("/{id}/conversations", GetContactConversations);
        group.MapGet("/", SearchContacts);
        group.MapPost("/", CreateContact);
        group.MapPut("/{id}", UpdateContact);
        group.MapDelete("/{id}", DeleteContact);
    }

    private static async Task<IResult> GetContact(
        string id,
        HttpContext context,
        IContactStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var contact = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return contact is null ? Results.NotFound() : Results.Ok(contact);
    }

    private static async Task<IResult> GetContactConversations(
        string id,
        HttpContext context,
        IConversationStore store,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var query = new ConversationQuery
        {
            ContactId = EntityId.From(id),
            Page = page,
            PageSize = pageSize,
        };
        var result = await store.ListAsync(tenantId, query, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> SearchContacts(
        HttpContext context,
        IContactStore store,
        string? search = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var result = await store.SearchAsync(tenantId, search, new PagedQuery(page, pageSize), ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateContact(
        HttpContext context,
        CreateContactRequest body,
        IContactStore store,
        IClock clock,
        CancellationToken ct)
    {
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
        return Results.Created($"/api/contacts/{contact.ContactId}", contact);
    }

    private static async Task<IResult> UpdateContact(
        string id,
        HttpContext context,
        UpdateContactRequest body,
        IContactStore store,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var contact = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (contact is null)
            return Results.NotFound();

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
        return Results.Ok(contact);
    }

    private static async Task<IResult> DeleteContact(
        string id,
        HttpContext context,
        IContactStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.DeleteAsync(tenantId, EntityId.From(id), ct);
        return Results.NoContent();
    }

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
    IReadOnlyDictionary<string, string>? CustomFields = null);

internal sealed record ChannelAddressDto(ChannelType Channel, string Address);
