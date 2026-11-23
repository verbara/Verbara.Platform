namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>The refusals of the tenant-id rule on the create routes (realtime-objects-stay-in-their-tenant, design D1).</summary>
internal static class TenantIdRefusal
{
    /// <summary>400: the id is not a lowercase slug, or is reserved.</summary>
    public const string Malformed =
        "Tenant id must be a lowercase slug (letters, digits, inner hyphens, at most 63 characters) and cannot be a reserved id.";

    /// <summary>409: the id overlaps a stored tenant id. Never names the stored tenant.</summary>
    public static string Unavailable(string tenantId) =>
        $"Tenant id '{tenantId}' is unavailable: it equals an existing tenant id, or one of them is the other followed by '-'.";
}
