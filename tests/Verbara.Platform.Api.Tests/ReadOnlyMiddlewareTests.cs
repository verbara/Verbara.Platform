using Verbara.Platform.Api.Middleware;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The read-only impersonation rule, <see cref="ImpersonationGuardMiddleware.IsBlockedInReadOnlyMode"/>,
/// on route templates: default-deny for every method but GET, HEAD and OPTIONS.
/// </summary>
public sealed class ReadOnlyMiddlewareTests
{
    private const string Api = "/api/v{version:apiVersion}";

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("get")]
    public void ReadOnlyMode_ShouldAllowReadMethods(string method)
    {
        var blocked = ImpersonationGuardMiddleware.IsBlockedInReadOnlyMode(method, Api + "/admin/queues");

        blocked.Should().BeFalse();
    }

    [Theory]
    [InlineData("PUT", Api + "/admin/queues/{id}")]
    [InlineData("DELETE", Api + "/admin/users/{id}")]
    [InlineData("PATCH", Api + "/management/retention/config")]
    [InlineData("POST", Api + "/admin/queues")]
    [InlineData("POST", Api + "/contacts/")]
    public void ReadOnlyMode_ShouldBlockWriteMethods(string method, string template)
    {
        var blocked = ImpersonationGuardMiddleware.IsBlockedInReadOnlyMode(method, template);

        blocked.Should().BeTrue();
    }

    [Theory]
    [InlineData("POST", Api + "/admin/gdpr/export")]
    [InlineData("POST", Api + "/contacts/search")]
    [InlineData("POST", Api + "/events/sse")]
    public void ReadOnlyMode_ShouldBlockPost_WhenPathOnlyLooksLikeARead(string method, string template)
    {
        // A segment such as "export", "search" or "sse" grants nothing: a POST is a write unless its
        // endpoint is allowed by name.
        var blocked = ImpersonationGuardMiddleware.IsBlockedInReadOnlyMode(method, template);

        blocked.Should().BeTrue();
    }

    [Fact]
    public void ReadOnlyMode_ShouldAllowEndImpersonation()
    {
        var blocked = ImpersonationGuardMiddleware.IsBlockedInReadOnlyMode("DELETE", Api + "/management/impersonate");

        blocked.Should().BeFalse();
    }

    [Fact]
    public void ReadOnlyMode_ShouldBlockStartImpersonation()
    {
        var blocked = ImpersonationGuardMiddleware.IsBlockedInReadOnlyMode("POST", Api + "/management/impersonate");

        blocked.Should().BeTrue(because: "only ending the session is allowed, not another verb on the same route");
    }

    [Fact]
    public void ReadOnlyMode_ShouldBlockWrite_WhenRequestMatchedNoEndpoint()
    {
        var blocked = ImpersonationGuardMiddleware.IsBlockedInReadOnlyMode("POST", template: null);

        blocked.Should().BeTrue();
    }
}
