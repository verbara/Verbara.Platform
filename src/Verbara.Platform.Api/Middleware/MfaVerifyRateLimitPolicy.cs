using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// Rate limit for the anonymous second-factor route (<c>POST /auth/mfa/verify</c>), applied through the
/// shared anonymous endpoint-filter limiter (<see cref="AnonymousRateLimitPolicy"/>). It runs before the
/// handler, so a rejected request never touches the challenge: no attempt is spent and no lockout recorded.
/// <para>
/// Two independent limits, both per client address (IPv6 by /64) in fixed one-minute windows:
/// </para>
/// <list type="number">
/// <item><c>mfa-verify</c> — per resolved tenant and client, from
/// <c>Auth:MfaVerify:RateLimit:PerMinutePerClient</c> (default <see cref="DefaultPerMinutePerClient"/>). A
/// request that resolves no tenant (the console usually sends no <c>X-Tenant-Id</c>) shares one
/// unknown-tenant partition per client.</item>
/// <item><c>mfa-verify-client</c> — per client across every tenant, from
/// <c>Auth:MfaVerify:RateLimit:AllTenantsPerMinutePerClient</c> (default
/// <see cref="DefaultAllTenantsPerMinutePerClient"/>). Tenant resolution takes the header tenant without
/// checking that it exists, so without this backstop a client naming a new <c>X-Tenant-Id</c> on every
/// request would get a fresh bucket each time.</item>
/// </list>
/// <para>
/// Sizing: one user needs at most three requests per challenge, so the binding case is not one user but one
/// address — a contact-center site whose agents sign in together at shift start behind a single egress
/// address. The default allows 100 users × 3 attempts per tenant and address per minute. That is per tenant
/// only for requests whose tenant resolves (by subdomain or a valid <c>X-Tenant-Id</c>): there the per-client
/// backstop, twice the per-tenant limit, lets a site hosting two tenants behind one address meet the
/// per-tenant limit first. Requests that resolve no tenant share the single unknown-tenant partition, so
/// every tenant behind that address shares one budget of the per-tenant default and the backstop never
/// binds; a site in that position sizes <c>PerMinutePerClient</c> for all of its users together.
/// The account lockout bounds guessing per account (each attempt is counted before its factor is checked);
/// these limits bound volume.
/// The first 429 per tenant is logged.
/// </para>
/// </summary>
internal static class MfaVerifyRateLimitPolicy
{
    internal const string Policy = "mfa-verify";
    internal const string AllTenantsPolicy = "mfa-verify-client";
    internal const string PerMinutePerClientKey = "Auth:MfaVerify:RateLimit:PerMinutePerClient";
    internal const string AllTenantsPerMinutePerClientKey = "Auth:MfaVerify:RateLimit:AllTenantsPerMinutePerClient";
    internal const int DefaultPerMinutePerClient = 300;
    internal const int DefaultAllTenantsPerMinutePerClient = 600;

    private const string Surface = "MFA verify";

    private static readonly AnonymousRateLimit PerTenantLimit =
        new(Policy, PerMinutePerClientKey, DefaultPerMinutePerClient, Surface);

    private static readonly AnonymousRateLimit AllTenantsLimit =
        new(AllTenantsPolicy, AllTenantsPerMinutePerClientKey, DefaultAllTenantsPerMinutePerClient, Surface);

    /// <summary>Applies both limits (per tenant and client, and per client across tenants) to the endpoint.</summary>
    internal static TBuilder RequireMfaVerifyRateLimit<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder
            .RequireAnonymousRateLimit(PerTenantLimit, static invocation =>
                ValueTask.FromResult(ResolvedTenant(invocation.HttpContext)))
            .RequireAnonymousRateLimit(AllTenantsLimit, static _ => ValueTask.FromResult<string?>(null));

    /// <summary>
    /// The tenant the request resolved to, or <see langword="null"/>, by the shared rule of
    /// <see cref="RateLimitPartitionKeys.TryGetResolvedTenant"/>: only a <see cref="TenantId"/> counts.
    /// </summary>
    private static string? ResolvedTenant(HttpContext context) =>
        RateLimitPartitionKeys.TryGetResolvedTenant(context, out var tenant) ? tenant.Value : null;
}
