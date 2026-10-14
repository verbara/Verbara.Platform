using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Pins the partition resolution of the <c>per-tenant</c> and <c>llm</c> rate-limit policies.
///
/// <para>
/// Both read the tenant through <see cref="RateLimitPartitionKeys.TryGetResolvedTenant"/>, which matches
/// <c>Items["TenantId"]</c> as a boxed <see cref="TenantId"/> — the type every writer stores. The readers used to
/// match <c>is string</c>, which never succeeds: <c>per-tenant</c> always fell into the shared <c>"__global__"</c>
/// key (Unlimited), and <c>llm</c> fell into it whenever no <c>X-Tenant-Id</c> header was sent (its raw-header
/// fallback partitioned the rest), including for <c>X-Tenant-Id: __global__</c>. These tests stub the real type and
/// pin namespaced keys: <c>t:{tenantId}</c> for a resolved tenant, <c>t?:</c> for an unknown one (never Unlimited),
/// <c>t:__global__</c> for a tenant named <c>__global__</c>, and for <c>llm</c> <c>key:{sha256}</c> for a request
/// carrying an API key, whatever tenant it names.
/// <c>per-tenant</c> is registered but attached to no endpoint; <c>llm</c> is attached to the AI-suggestion route.
/// </para>
/// </summary>
public sealed class TenantRateLimitPolicyTests
{
    // Faithful seam: TenantResolutionMiddleware writes Items["TenantId"] as a boxed TenantId struct
    // (`context.Items["TenantId"] = tenantId.Value` on a TenantId?), never a string — see T1-D1.
    private static DefaultHttpContext ContextFor(string? tenantId, TenantTierCache? tierCache = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(tierCache ?? new TenantTierCache());
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (tenantId is not null)
            context.Items["TenantId"] = new TenantId(tenantId);
        return context;
    }

    [Fact]
    public void ResolvePerTenantTarget_ShouldPartitionTenantsIndependently_WhenTwoTenantsResolved()
    {
        var cache = new TenantTierCache();
        cache.SetTier("tenant-a", RateLimitTier.Standard);
        cache.SetTier("tenant-b", RateLimitTier.Standard);

        var a = TenantRateLimitPolicy.ResolvePerTenantTarget(ContextFor("tenant-a", cache));
        var b = TenantRateLimitPolicy.ResolvePerTenantTarget(ContextFor("tenant-b", cache));

        a.PartitionKey.Should().Be("t:tenant-a");
        b.PartitionKey.Should().Be("t:tenant-b");
        a.Tier.IsUnlimited().Should().BeFalse(
            because: "a resolved tenant on a limited tier must actually be throttled");
        b.Tier.IsUnlimited().Should().BeFalse(
            because: "a resolved tenant on a limited tier must actually be throttled");
    }

    [Fact]
    public void ResolvePerTenantTarget_ShouldUseConfiguredTier_WhenTierCacheHasTenant()
    {
        var cache = new TenantTierCache();
        cache.SetTier("tenant-a", RateLimitTier.Enterprise);

        var target = TenantRateLimitPolicy.ResolvePerTenantTarget(ContextFor("tenant-a", cache));

        target.PartitionKey.Should().Be("t:tenant-a");
        target.Tier.Should().Be(RateLimitTier.Enterprise);
    }

    [Fact]
    public void ResolvePerTenantTarget_ShouldFallBackToUnknownStandard_WhenTenantUnresolved()
    {
        var target = TenantRateLimitPolicy.ResolvePerTenantTarget(ContextFor(tenantId: null));

        target.PartitionKey.Should().Be("t?:");
        target.Tier.Should().Be(
            RateLimitTier.Standard,
            because: "an unknown tenant is limited at least as strictly as a known one (fail closed), never Unlimited");
    }

    [Fact]
    public void ResolvePerTenantTarget_ShouldTreatSentinelAsOrdinaryTenant_WhenTenantIdIsGlobal()
    {
        var target = TenantRateLimitPolicy.ResolvePerTenantTarget(ContextFor("__global__"));

        target.PartitionKey.Should().Be("t:__global__");
        target.Tier.IsUnlimited().Should().BeFalse(
            because: "a tenant id a caller can choose must never select an unlimited fallback bucket");
    }

    [Fact]
    public void ResolveLlmTarget_ShouldPartitionPerTenantAtLlmLimit_WhenTenantResolved()
    {
        var target = TenantRateLimitPolicy.ResolveLlmTarget(ContextFor("tenant-a"));

        target.PartitionKey.Should().Be("t:tenant-a");
        target.PermitLimit.Should().Be(TenantRateLimitPolicy.LlmPermitLimit);
    }

    [Fact]
    public void ResolveLlmTarget_ShouldUseUnknownPartitionAtFallbackLimit_WhenTenantUnresolved()
    {
        var context = ContextFor(tenantId: null);
        // The request header is not a second resolution path: only Items["TenantId"] counts.
        context.Request.Headers["X-Tenant-Id"] = "tenant-a";

        var target = TenantRateLimitPolicy.ResolveLlmTarget(context);

        target.PartitionKey.Should().Be("t?:");
        target.PermitLimit.Should().Be(TenantRateLimitPolicy.GlobalFallbackPermitLimit);
    }

    [Fact]
    public void ResolveLlmTarget_ShouldTreatSentinelAsOrdinaryTenant_WhenTenantIdIsGlobal()
    {
        var target = TenantRateLimitPolicy.ResolveLlmTarget(ContextFor("__global__"));

        target.PartitionKey.Should().Be("t:__global__");
        target.PermitLimit.Should().Be(TenantRateLimitPolicy.LlmPermitLimit);
    }

    [Fact]
    public void ResolveLlmTarget_ShouldTreatTenantAsUnresolved_WhenItemIsString()
    {
        var context = ContextFor(tenantId: null);
        context.Items["TenantId"] = "tenant-a";

        var target = TenantRateLimitPolicy.ResolveLlmTarget(context);

        target.PartitionKey.Should().Be(
            "t?:", because: "Items[\"TenantId\"] is a boxed TenantId; any other type is an unresolved tenant");
    }

    [Fact]
    public void ResolveLlmTarget_ShouldPartitionOnApiKey_WhenRequestCarriesApiKeyCredential()
    {
        // The API-key handler later replaces Items["TenantId"] with the key's tenant, so the tenant named before
        // authentication does not decide whose LLM budget is spent: two named tenants, one key, one bucket.
        var expected = "key:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("vk_live_abc")));

        var first = ContextFor("tenant-a");
        first.Request.Headers.Authorization = "Bearer vk_live_abc";
        var second = ContextFor("tenant-b");
        second.Request.Headers.Authorization = "Bearer vk_live_abc";

        var a = TenantRateLimitPolicy.ResolveLlmTarget(first);
        var b = TenantRateLimitPolicy.ResolveLlmTarget(second);

        a.PartitionKey.Should().Be(expected);
        b.PartitionKey.Should().Be(expected);
        a.PermitLimit.Should().Be(TenantRateLimitPolicy.LlmPermitLimit);
    }

    [Fact]
    public void ResolveLlmTarget_ShouldPartitionOnTenant_WhenBearerIsJwt()
    {
        // A JWT is routed to the JWT scheme, which never overwrites a resolved tenant, and
        // TenantBoundaryValidationMiddleware refuses a mismatched one: the resolved tenant stays the key.
        var context = ContextFor("tenant-a");
        context.Request.Headers.Authorization = "Bearer eyJhbGciOiJSUzI1NiJ9.e30.sig";

        var target = TenantRateLimitPolicy.ResolveLlmTarget(context);

        target.PartitionKey.Should().Be("t:tenant-a");
    }
}
