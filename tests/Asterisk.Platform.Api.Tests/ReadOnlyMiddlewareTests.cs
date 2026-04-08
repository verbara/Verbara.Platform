namespace Asterisk.Platform.Api.Tests;

public sealed class ReadOnlyMiddlewareTests
{
    // Mirror of TenantResolutionMiddleware.IsBlockedInReadOnlyMode for pure-logic testing.
    private static bool IsBlockedInReadOnlyMode(string method, string path)
    {
        // GET, HEAD, OPTIONS always allowed
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            return false;

        // DELETE /management/impersonate always allowed (end session)
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            && (path.Equals("/api/v1/management/impersonate", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/management/impersonate", StringComparison.OrdinalIgnoreCase)))
            return false;

        // Block all other DELETE, PUT, PATCH
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase))
            return true;

        // POST: allow safe read-only operations
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/sse", StringComparison.OrdinalIgnoreCase))
                return false;
            if (path.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return false;
            if (path.Contains("/export", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        return false;
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void ReadOnlyMode_ShouldAllowReadMethods(string method)
    {
        var blocked = IsBlockedInReadOnlyMode(method, "/api/v1/admin/queues");

        blocked.Should().BeFalse();
    }

    [Theory]
    [InlineData("PUT", "/api/v1/admin/queues/q1")]
    [InlineData("DELETE", "/api/v1/admin/users/u1")]
    [InlineData("PATCH", "/api/v1/admin/contacts/c1")]
    public void ReadOnlyMode_ShouldBlockWriteMethods(string method, string path)
    {
        var blocked = IsBlockedInReadOnlyMode(method, path);

        blocked.Should().BeTrue();
    }

    [Theory]
    [InlineData("POST", "/api/v1/sse/events")]
    [InlineData("POST", "/api/v1/contacts/search")]
    [InlineData("POST", "/api/v1/gdpr/contacts/c1/export")]
    public void ReadOnlyMode_ShouldAllowSafePostEndpoints(string method, string path)
    {
        var blocked = IsBlockedInReadOnlyMode(method, path);

        blocked.Should().BeFalse();
    }

    [Theory]
    [InlineData("POST", "/api/v1/admin/queues")]
    [InlineData("POST", "/api/v1/admin/users")]
    [InlineData("POST", "/api/v1/management/tenants")]
    public void ReadOnlyMode_ShouldBlockUnsafePostEndpoints(string method, string path)
    {
        var blocked = IsBlockedInReadOnlyMode(method, path);

        blocked.Should().BeTrue();
    }

    [Fact]
    public void ReadOnlyMode_ShouldAllowEndImpersonation()
    {
        var blocked = IsBlockedInReadOnlyMode("DELETE", "/api/v1/management/impersonate");

        blocked.Should().BeFalse();
    }
}
