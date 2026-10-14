using Verbara.Platform.Api.Auth;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// The one reader of the resolved tenant for rate limiting, and the builders of the namespaced partition keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>Items["TenantId"]</c> rule:</b> the value is always a boxed <see cref="TenantId"/> (written by
/// <see cref="TenantResolutionMiddleware"/>, the JWT bearer events and the API-key handler). A reader matches
/// <c>is TenantId</c>; anything else — absent, a <see cref="string"/>, any other type — means the tenant is
/// <b>unresolved</b>, never a string to coerce and never a reason to exempt. Any further fact about the
/// resolution (for example its source) goes under its own <c>Items</c> key, never folded into this one, or
/// every reader silently stops matching.
/// </para>
/// <para>
/// Keys: <c>t:{tenantId}</c> for a resolved tenant; <c>t?:</c> for an unknown tenant (no tenant component, so
/// no client input can produce it); <c>key:{sha256}</c> for a request carrying an API key (the <c>llm</c>
/// policy only); <c>ip:{address}</c> is reserved for a per-client-IP partition and unused.
/// A client-supplied tenant id such as <c>__global__</c> is an ordinary tenant: <c>t:__global__</c>.
/// </para>
/// </remarks>
internal static class RateLimitPartitionKeys
{
    /// <summary>Key of the partition for requests whose tenant is unknown.</summary>
    internal const string UnknownTenant = "t?:";

    /// <summary>Reads <c>Items["TenantId"]</c> as a <see cref="TenantId"/>; <see langword="false"/> means unresolved.</summary>
    internal static bool TryGetResolvedTenant(HttpContext context, out TenantId tenantId)
    {
        if (context.Items.TryGetValue("TenantId", out var value) && value is TenantId resolved)
        {
            tenantId = resolved;
            return true;
        }

        tenantId = default;
        return false;
    }

    /// <summary>
    /// Recognises a request the API-key scheme will authenticate — a non-JWT <c>Bearer</c> credential, the same
    /// test as <c>AuthSchemeConfiguration</c>'s selector (a JWT starts with <c>eyJ</c>) and the same reader as
    /// <see cref="ApiKeyAuthenticationHandler"/> — and returns <c>key:{sha256 of the key}</c>. The handler later
    /// replaces <c>Items["TenantId"]</c> with the key's own tenant, so the tenant the request named before
    /// authentication does not decide whose budget is spent; the key does. An invalid key gets its own bucket
    /// and a <c>401</c>, never the protected work.
    /// </summary>
    internal static bool TryGetApiKeyPartition(HttpContext context, out string partitionKey)
    {
        if (ApiKeyAuthenticationHandler.TryReadBearerCredential(context.Request, out var credential)
            && !credential.StartsWith("eyJ", StringComparison.Ordinal))
        {
            partitionKey = "key:" + ApiKeyAuthenticationHandler.HashKey(credential);
            return true;
        }

        partitionKey = string.Empty;
        return false;
    }

    /// <summary>Key of the partition for the resolved <paramref name="tenantId"/>.</summary>
    internal static string ForTenant(TenantId tenantId) => "t:" + tenantId.Value;
}
