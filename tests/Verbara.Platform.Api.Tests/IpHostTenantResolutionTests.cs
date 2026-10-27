using System.Security.Claims;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Regression suite for OpenSpec change <c>fix-ip-host-tenant-resolution</c>.
/// <c>TenantResolutionMiddleware.ResolveFromSubdomainAsync</c> split the request host on its
/// first <c>.</c>, so an IP-address host (<c>127.0.0.1</c>, <c>192.168.1.50</c>) resolved the
/// phantom tenant <c>"127"</c> / <c>"192"</c> — and because the subdomain runs BEFORE the
/// <c>X-Tenant-Id</c> header, a correct header could not override it. The reserved-label
/// exclusion (<c>www</c>/<c>api</c>/<c>localhost</c>) was also case-sensitive.
/// </summary>
/// <remarks>
/// Same <see cref="DefaultHttpContext"/> shape as <see cref="SubdomainResolutionTests"/>: the
/// only public observable of the middleware is the boxed <see cref="TenantId"/> in
/// <c>Items["TenantId"]</c>.
/// </remarks>
public sealed class IpHostTenantResolutionTests
{
    private readonly ITenantBrandingStore _brandingStore = Substitute.For<ITenantBrandingStore>();
    private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();
    private bool _nextCalled;
    private TenantId? _tenantSeenDownstream;

    private TenantResolutionMiddleware CreateMiddleware()
        => new(_ =>
        {
            _nextCalled = true;
            return Task.CompletedTask;
        });

    private DefaultHttpContext CreateContext(string host, string? tenantHeader = null, string path = "/api/v1/conversations")
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (tenantHeader is not null)
            context.Request.Headers["X-Tenant-Id"] = tenantHeader;

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ITenantBrandingStore)).Returns(_brandingStore);
        sp.GetService(typeof(ITenantStore)).Returns(_tenantStore);
        context.RequestServices = sp;

        return context;
    }

    // ─── 4.1 One case per source — green before AND after the fix ───────────

    [Fact]
    public async Task InvokeAsync_ShouldResolveWebhookPathTenant_WhenPathIsInboundWebhook()
    {
        // The webhook path outranks both subdomain and header.
        var context = CreateContext("localhost", tenantHeader: "other", path: "/api/v1/webhooks/acme/whatsapp");

        await CreateMiddleware().InvokeAsync(context);

        _nextCalled.Should().BeTrue();
        context.Items["TenantId"].Should().Be(new TenantId("acme"));
        SourceOf(context).Should().Be(TenantSource.WebhookPath);
    }

    [Fact]
    public async Task InvokeAsync_ShouldResolveBrandingTenant_WhenHostIsMappedSubdomain()
    {
        _brandingStore
            .GetBySubdomainAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new TenantBranding { TenantId = "tenant-acme", Subdomain = "acme" });

        // A white-label subdomain pins the tenant: it outranks the header (ADR-0038).
        var context = CreateContext("acme.platform.example", tenantHeader: "other");

        await CreateMiddleware().InvokeAsync(context);

        _nextCalled.Should().BeTrue();
        context.Items["TenantId"].Should().Be(new TenantId("tenant-acme"));
        SourceOf(context).Should().Be(TenantSource.Subdomain);
    }

    [Fact]
    public async Task InvokeAsync_ShouldFallbackToSubdomainLabel_WhenHostnameSubdomainIsUnmapped()
    {
        // Accepted residual (design "Accepted residual", ADR-0038): the backward-compat fallback
        // still turns an unmapped first label of a dotted NON-IP hostname into a tenant id.
        // Pinned here so the IP guard is seen not to widen into a hostname behaviour change.
        _brandingStore
            .GetBySubdomainAsync("unknown", Arg.Any<CancellationToken>())
            .Returns((TenantBranding?)null);

        var context = CreateContext("unknown.platform.example", tenantHeader: "acme");

        await CreateMiddleware().InvokeAsync(context);

        _nextCalled.Should().BeTrue();
        context.Items["TenantId"].Should().Be(new TenantId("unknown"));
        SourceOf(context).Should().Be(TenantSource.Subdomain);
    }

    [Fact]
    public async Task InvokeAsync_ShouldResolveHeaderTenant_WhenHostHasNoSubdomain()
    {
        var context = CreateContext("localhost:5199", tenantHeader: "acme");

        await CreateMiddleware().InvokeAsync(context);

        _nextCalled.Should().BeTrue();
        context.Items["TenantId"].Should().Be(new TenantId("acme"));
        SourceOf(context).Should().Be(TenantSource.Header);
    }

    // ─── 4.2 Bare IPv4 host — red before the fix ────────────────────────────

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:5199")]
    [InlineData("192.168.1.50")]
    [InlineData("192.168.1.50:8080")]
    [InlineData("127.0.0.1.")]
    public async Task InvokeAsync_ShouldResolveHeaderTenant_WhenHostIsIpv4Literal(string host)
    {
        // Before the fix the first octet ("127", "192") was taken as a subdomain and won over
        // the header. The trailing-dot form must not slip past the guard either.
        var context = CreateContext(host, tenantHeader: "acme");

        await CreateMiddleware().InvokeAsync(context);

        _nextCalled.Should().BeTrue();
        context.Items["TenantId"].Should().Be(
            new TenantId("acme"),
            because: "an IP-address host carries no subdomain, so the X-Tenant-Id header is the source");
        SourceOf(context).Should().Be(TenantSource.Header);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.50:8080")]
    [InlineData("127.0.0.1.")]
    public async Task InvokeAsync_ShouldResolveNoTenant_WhenHostIsIpv4LiteralAndNoHeader(string host)
    {
        var context = CreateContext(host);

        await CreateMiddleware().InvokeAsync(context);

        _nextCalled.Should().BeTrue();
        context.Items.ContainsKey("TenantId").Should().BeFalse(
            because: "no label of an IP-address host may be read as a tenant id");
        context.Items.ContainsKey(TenantSourceItems.ItemKey).Should().BeFalse(
            because: "the source is written under the same non-null guard as the tenant");
    }

    private static TenantSource? SourceOf(HttpContext context)
        => TenantSourceItems.TryGet(context, out var source) ? source : null;

    // ─── 4.3 Dotted IPv6 literal — red before the fix ───────────────────────
    //
    // NOT "[::1]": it has no dot, so the IndexOf('.') early-out already returns null today and
    // a test on it would pin nothing. HostString.Host keeps the brackets.

    [Theory]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("[::ffff:192.168.1.50]:5199")]
    public async Task InvokeAsync_ShouldResolveHeaderTenant_WhenHostIsDottedIpv6Literal(string host)
    {
        var context = CreateContext(host, tenantHeader: "acme");

        await CreateMiddleware().InvokeAsync(context);

        _nextCalled.Should().BeTrue();
        context.Items["TenantId"].Should().Be(
            new TenantId("acme"),
            because: "an IPv4-mapped IPv6 literal is an IP address, not a hostname with a subdomain");
    }

    // ─── 4.4 Uppercase reserved labels — red before the fix ─────────────────

    [Theory]
    [InlineData("WWW.platform.example")]
    [InlineData("API.platform.example")]
    [InlineData("Www.platform.example")]
    public async Task InvokeAsync_ShouldResolveHeaderTenant_WhenReservedLabelIsNotLowercase(string host)
    {
        var context = CreateContext(host, tenantHeader: "acme");

        await CreateMiddleware().InvokeAsync(context);

        _nextCalled.Should().BeTrue();
        context.Items["TenantId"].Should().Be(
            new TenantId("acme"),
            because: "DNS labels are case-insensitive, so www/api must be excluded in any case");
    }

    // ─── 4.5 Platform / Partner principal on an IP host — red before the fix ─
    //
    // Runs TenantResolution → TenantBoundaryValidation and records the tenant visible when the
    // endpoint (the boundary middleware's _next) runs. Before the fix the Platform caller is NOT
    // rejected: the boundary middleware lets Platform through a mismatch without correcting
    // Items["TenantId"], so the endpoint silently operates on the phantom tenant "127".

    private async Task RunPipelineAsPrincipalAsync(DefaultHttpContext context, string jwtTid)
    {
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tid", jwtTid)], "Bearer"));

        var boundary = new TenantBoundaryValidationMiddleware(ctx =>
        {
            _nextCalled = true;
            _tenantSeenDownstream = ctx.Items.TryGetValue("TenantId", out var raw) && raw is TenantId t ? t : null;
            return Task.CompletedTask;
        });
        var resolution = new TenantResolutionMiddleware(boundary.InvokeAsync);

        await resolution.InvokeAsync(context);
    }

    private void SeedTenant(string id, TenantType type, string? parent)
        => _tenantStore
            .GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(new Tenant
            {
                TenantId = id,
                Name = id,
                Status = TenantStatus.Active,
                Type = type,
                ParentTenantId = parent,
            });

    [Theory]
    [InlineData("127.0.0.1:5199")]
    [InlineData("192.168.1.50")]
    public async Task Pipeline_ShouldScopePlatformPrincipalToHeaderTenant_WhenHostIsIpv4Literal(string host)
    {
        SeedTenant("platform", TenantType.Platform, parent: null);
        SeedTenant("acme", TenantType.Customer, parent: "platform");
        var context = CreateContext(host, tenantHeader: "acme");

        await RunPipelineAsPrincipalAsync(context, jwtTid: "platform");

        _nextCalled.Should().BeTrue();
        _tenantSeenDownstream.Should().Be(
            new TenantId("acme"),
            because: "a Platform principal on an IP host must operate on the tenant it named, never one taken from the host address");
    }

    [Fact]
    public async Task Pipeline_ShouldScopePartnerPrincipalToOwnCustomer_WhenHostIsIpv4Literal()
    {
        // A Partner reaching into its own subtree is allowed — but only when the resolved tenant
        // is the one it named. With the phantom "127" the hierarchy walk fails closed and the
        // Partner is rejected for a request that is legitimate.
        SeedTenant("platform", TenantType.Platform, parent: null);
        SeedTenant("partner-zeta", TenantType.Partner, parent: "platform");
        SeedTenant("partner-zeta-customer", TenantType.Customer, parent: "partner-zeta");
        var context = CreateContext("127.0.0.1:5199", tenantHeader: "partner-zeta-customer");

        await RunPipelineAsPrincipalAsync(context, jwtTid: "partner-zeta");

        context.Response.StatusCode.Should().NotBe(StatusCodes.Status403Forbidden);
        _tenantSeenDownstream.Should().Be(new TenantId("partner-zeta-customer"));
    }

    // ─── 4.7 A legitimate 403 explains itself (spec Requirement 4) ──────────
    //
    // Drives TenantBoundaryValidationMiddleware directly with Items["TenantId"] seeded, so the
    // "no source recorded" scenario — a writer that records none — is reachable.

    private async Task<string> RunBoundaryAsCustomerAsync(DefaultHttpContext context)
    {
        SeedTenant("acme", TenantType.Customer, parent: "platform");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tid", "acme")], "Bearer"));

        var boundary = new TenantBoundaryValidationMiddleware(_ =>
        {
            _nextCalled = true;
            return Task.CompletedTask;
        });
        await boundary.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _nextCalled.Should().BeFalse();
        context.Response.Body.Position = 0;
        using var json = await System.Text.Json.JsonDocument.ParseAsync(context.Response.Body);
        return json.RootElement.GetProperty("error").GetString()!;
    }

    [Theory]
    [InlineData(nameof(TenantSource.WebhookPath), "webhook-path")]
    [InlineData(nameof(TenantSource.Subdomain), "subdomain")]
    [InlineData(nameof(TenantSource.Header), "header")]
    [InlineData(nameof(TenantSource.JwtFallback), "jwt")]
    [InlineData(nameof(TenantSource.ApiKeyBinding), "api-key")]
    public async Task TenantBoundary_ShouldNameResolvedTenantAndSource_WhenSourceIsRecorded(string sourceName, string token)
    {
        // By name: TenantSource is internal, so it cannot be a public test method's parameter type.
        var source = Enum.Parse<TenantSource>(sourceName);
        var context = CreateContext("localhost");
        context.Items["TenantId"] = new TenantId("globex");
        TenantSourceItems.Set(context, source);

        var body = await RunBoundaryAsCustomerAsync(context);

        body.Should().Be($"Tenant globex resolved from {token} does not match authenticated principal.");
        body.Should().NotContain("acme", because: "the 403 names only the tenant resolved from the request");
    }

    [Fact]
    public async Task TenantBoundary_ShouldNameTenantWithoutSource_WhenNoSourceIsRecorded()
    {
        var context = CreateContext("localhost");
        context.Items["TenantId"] = new TenantId("globex");

        var body = await RunBoundaryAsCustomerAsync(context);

        body.Should().Be("Tenant globex does not match authenticated principal.");
        body.Should().NotContain("resolved from", because: "an unrecorded source must not be attributed to one");
        body.Should().NotContain("acme");
    }
}
