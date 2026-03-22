using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ContactEndpoints
{
    public static void MapContactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contacts").RequireAuthorization();

        group.MapGet("/{id}", GetContact);
        group.MapGet("/{id}/conversations", GetContactConversations);
        group.MapGet("/", SearchContacts);
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

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}
