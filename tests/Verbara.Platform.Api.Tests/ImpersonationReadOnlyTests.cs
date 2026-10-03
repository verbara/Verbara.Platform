using System.Collections.Frozen;
using Verbara.Platform.Api.Endpoints;

namespace Verbara.Platform.Api.Tests;

public sealed class ImpersonationReadOnlyTests
{
    // The production set StartImpersonation filters a read-only session's permissions through.
    private static readonly FrozenSet<string> ReadOnlyPermissions = ManagementImpersonationEndpoints.ReadOnlyPermissions;

    private static HashSet<string> ApplyReadOnlyFilter(IEnumerable<string> callerPermissions, bool readOnly)
    {
        var nonPlatformPerms = callerPermissions
            .Where(p => !p.StartsWith("platform:", StringComparison.Ordinal));

        return readOnly
            ? new HashSet<string>(nonPlatformPerms.Where(p => ReadOnlyPermissions.Contains(p)))
            : new HashSet<string>(nonPlatformPerms);
    }

    [Fact]
    public void ReadOnlyFilter_ShouldKeepOnlyViewPermissions()
    {
        var callerPermissions = new[]
        {
            "contacts:contact:view",
            "contacts:contact:create",
            "contacts:contact:delete",
            "queues:queue:view",
            "queues:queue:update",
            "users:user:view",
            "users:user:delete",
        };

        var result = ApplyReadOnlyFilter(callerPermissions, readOnly: true);

        result.Should().BeEquivalentTo(
            "contacts:contact:view",
            "queues:queue:view",
            "users:user:view");
        result.Should().NotContain("contacts:contact:create");
        result.Should().NotContain("contacts:contact:delete");
        result.Should().NotContain("queues:queue:update");
        result.Should().NotContain("users:user:delete");
    }

    [Fact]
    public void ReadOnlyFilter_ShouldExcludePlatformPermissions()
    {
        var callerPermissions = new[]
        {
            "platform:tenant:view",
            "platform:tenant:impersonate",
            "platform:system:manage",
            "contacts:contact:view",
            "users:user:view",
        };

        var result = ApplyReadOnlyFilter(callerPermissions, readOnly: true);

        result.Should().NotContain("platform:tenant:view");
        result.Should().NotContain("platform:tenant:impersonate");
        result.Should().NotContain("platform:system:manage");
        result.Should().Contain("contacts:contact:view");
        result.Should().Contain("users:user:view");
    }

    [Fact]
    public void ReadOnlyFilter_ShouldIncludeMonitorPermission()
    {
        var callerPermissions = new[]
        {
            "contacts:conversation:monitor",
            "contacts:conversation:close",
        };

        var result = ApplyReadOnlyFilter(callerPermissions, readOnly: true);

        result.Should().Contain("contacts:conversation:monitor");
        result.Should().NotContain("contacts:conversation:close");
    }

    [Fact]
    public void ReadOnlyFilter_ShouldIncludeExportPermissions()
    {
        var callerPermissions = new[]
        {
            "reporting:historical:export",
            "recording:recording:export",
            "analytics:cdr:export",
            "reporting:historical:delete",
        };

        var result = ApplyReadOnlyFilter(callerPermissions, readOnly: true);

        result.Should().Contain("reporting:historical:export");
        result.Should().Contain("recording:recording:export");
        result.Should().Contain("analytics:cdr:export");
        result.Should().NotContain("reporting:historical:delete");
    }

    [Fact]
    public void FullModeFilter_ShouldKeepAllNonPlatformPermissions()
    {
        var callerPermissions = new[]
        {
            "platform:tenant:impersonate",
            "contacts:contact:view",
            "contacts:contact:create",
            "contacts:contact:delete",
            "users:user:view",
            "users:user:delete",
            "campaigns:campaign:update",
        };

        var result = ApplyReadOnlyFilter(callerPermissions, readOnly: false);

        result.Should().NotContain("platform:tenant:impersonate");
        result.Should().Contain("contacts:contact:view");
        result.Should().Contain("contacts:contact:create");
        result.Should().Contain("contacts:contact:delete");
        result.Should().Contain("users:user:view");
        result.Should().Contain("users:user:delete");
        result.Should().Contain("campaigns:campaign:update");
    }

    [Fact]
    public void ReadOnlyPermissionSet_ShouldHave23Entries()
    {
        ReadOnlyPermissions.Count.Should().Be(23);
    }

    [Fact]
    public void ReadOnlyFilter_ShouldIncludeCreditReadPermission()
    {
        var callerPermissions = new[]
        {
            "billing:credits:read",
            "billing:credits:grant",
            "features:agent-assist:manage",
        };

        var result = ApplyReadOnlyFilter(callerPermissions, readOnly: true);

        result.Should().BeEquivalentTo(["billing:credits:read"],
            because: "the credit balance is a read; granting credits and managing a feature are not");
    }

    [Fact]
    public void ReadOnlyPermissionSet_ShouldHoldOnlyReads()
    {
        // A permission gate passes an impersonation token only on a minted permission, so a write or
        // manage permission here would let a read-only session through a gate the moment anything
        // else stopped refusing its writes.
        string[] readActions = ["view", "read", "export", "play", "monitor"];

        ReadOnlyPermissions.Should().OnlyContain(p => readActions.Contains(p.Split(':', StringSplitOptions.None)[2]));
    }
}
