namespace Asterisk.Platform.Api.Tests;

public sealed class ImpersonationReadOnlyTests
{
    // Mirror of ManagementImpersonationEndpoints.ReadOnlyPermissions for test assertions.
    private static readonly HashSet<string> ReadOnlyPermissions = new(StringComparer.Ordinal)
    {
        "contacts:contact:view",
        "contacts:conversation:monitor",
        "queues:queue:view",
        "users:user:view",
        "campaigns:campaign:view",
        "reporting:realtime:view",
        "reporting:historical:view",
        "reporting:historical:export",
        "quality:evaluation:view",
        "recording:recording:play",
        "recording:recording:export",
        "routing:skill:view",
        "routing:flow:view",
        "analytics:cdr:view",
        "analytics:cdr:export",
        "analytics:interval:view",
        "system:audit:view",
        "agentassist:session:view",
        "callanalytics:analysis:view",
        "partner:customer:view",
        "partner:billing:view",
        "partner:settings:view",
    };

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
    public void ReadOnlyPermissionSet_ShouldHave22Entries()
    {
        ReadOnlyPermissions.Count.Should().Be(22);
    }
}
