using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Api.Tests.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Impersonation;

/// <summary>
/// The impersonation guards against the host's real route table. The guards match route templates,
/// so a renamed route would silently drop out of a list: every entry must name an endpoint that exists.
/// And the read-only rule is default-deny: the only write endpoint a read-only session reaches is the
/// one allowed on purpose, so a write endpoint added later is refused until someone allows it.
/// </summary>
public sealed class ImpersonationGuardRouteTableTests : IClassFixture<ImpersonationApiFactory>
{
    private const string Api = "/api/v{version:apiVersion}";

    private readonly ImpersonationApiFactory _factory;

    public ImpersonationGuardRouteTableTests(ImpersonationApiFactory factory) => _factory = factory;

    [Fact]
    public void IsBlockedInReadOnlyMode_ShouldBlockEveryWriteEndpoint_WhenNotExplicitlyAllowed()
    {
        var allowed = Routes()
            .Where(r => !ImpersonationGuardMiddleware.IsBlockedInReadOnlyMode(r.Method, r.Template))
            .Where(r => r.Method is not ("GET" or "HEAD" or "OPTIONS"))
            .Select(r => $"{r.Method} {r.Template}")
            .ToList();

        allowed.Should().BeEquivalentTo([$"DELETE {Api}/management/impersonate"],
            because: "ending the session is the only write a read-only session may make");
    }

    [Fact]
    public void AllowedInReadOnly_ShouldNameARealEndpoint_WhenListed()
    {
        var routes = Routes().Select(r => $"{r.Method} {r.Template}").ToHashSet(StringComparer.Ordinal);

        ImpersonationGuardMiddleware.AllowedInReadOnly.Should().OnlyContain(entry => routes.Contains(entry),
            because: "an allowlist entry that matches no endpoint is a route someone renamed");
    }

    [Fact]
    public void BlockedDuringImpersonation_ShouldNameARealEndpoint_WhenListed()
    {
        var routes = Routes().Select(r => $"{r.Method} {r.Template}").ToHashSet(StringComparer.Ordinal);

        ImpersonationGuardMiddleware.BlockedDuringImpersonation.Should().OnlyContain(entry => routes.Contains(entry),
            because: "a guard entry that matches no endpoint guards nothing");
    }

    [Fact]
    public void BlockedPrefixesDuringImpersonation_ShouldCoverARealEndpoint_WhenListed()
    {
        var routes = Routes();

        foreach (var (method, prefix) in ImpersonationGuardMiddleware.BlockedPrefixesDuringImpersonation)
        {
            routes.Should().Contain(r => r.Method == method && r.Template.StartsWith(prefix, StringComparison.Ordinal),
                because: $"the prefix rule {method} {prefix}* must cover at least one endpoint");
        }
    }

    [Theory]
    [InlineData("POST", Api + "/management/impersonate")]
    [InlineData("POST", Api + "/setup")]
    [InlineData("POST", Api + "/auth/change-password")]
    [InlineData("POST", Api + "/auth/mfa/setup")]
    [InlineData("POST", Api + "/auth/mfa/confirm")]
    [InlineData("DELETE", Api + "/auth/mfa")]
    [InlineData("POST", Api + "/auth/mfa/recovery-codes/regenerate")]
    [InlineData("POST", Api + "/auth/sessions/revoke-others")]
    [InlineData("DELETE", Api + "/auth/sessions/{tokenId}")]
    [InlineData("POST", Api + "/profile/security/mfa/enroll/init")]
    [InlineData("POST", Api + "/profile/security/mfa/enroll/verify")]
    [InlineData("POST", Api + "/profile/security/mfa/enroll/complete")]
    [InlineData("POST", Api + "/profile/security/recovery-codes/regenerate")]
    [InlineData("POST", Api + "/profile/security/sessions/{tokenId}/revoke")]
    [InlineData("DELETE", Api + "/admin/auth/sessions/{id}")]
    [InlineData("DELETE", Api + "/admin/auth/sessions/by-user/{userId}")]
    [InlineData("DELETE", Api + "/management/tenants/{id}")]
    [InlineData("PUT", Api + "/management/system/settings")]
    [InlineData("PUT", Api + "/management/system/license")]
    public void IsBlockedDuringImpersonation_ShouldBlockOperation_WhenOperationIsAccountOrInstallationSecurity(string method, string template)
    {
        Routes().Should().Contain((method, template), because: "the operation must exist for refusing it to mean anything");
        ImpersonationGuardMiddleware.IsBlockedDuringImpersonation(method, template).Should().BeTrue();
    }

    [Theory]
    [InlineData("GET", Api + "/auth/sessions")]
    [InlineData("POST", Api + "/auth/mfa/verify")]
    [InlineData("DELETE", Api + "/management/impersonate")]
    [InlineData("POST", Api + "/contacts/")]
    [InlineData("GET", Api + "/management/system/settings")]
    public void IsBlockedDuringImpersonation_ShouldAllowOperation_WhenOperationIsNotListed(string method, string template)
    {
        Routes().Should().Contain((method, template));
        ImpersonationGuardMiddleware.IsBlockedDuringImpersonation(method, template).Should().BeFalse();
    }

    // Every (method, template) the host routes. An endpoint that accepts any method is listed as POST,
    // a write the read-only rule must refuse like any other.
    private List<(string Method, string Template)> Routes() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["POST"])
                .Select(method => (method, e.RoutePattern.RawText ?? string.Empty)))
            .ToList();
}
