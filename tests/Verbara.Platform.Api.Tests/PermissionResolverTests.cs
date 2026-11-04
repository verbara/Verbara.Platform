using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public sealed class PermissionResolverTests
{
    private readonly IUserRoleStore _userRoleStore = Substitute.For<IUserRoleStore>();
    private readonly PermissionResolver _sut;
    private readonly TenantId _tenantId = new("tenant-1");
    private readonly EntityId _userId = EntityId.From("user-1");

    public PermissionResolverTests()
    {
        _sut = new PermissionResolver(_userRoleStore);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnPermissions_WhenUserHasRoles()
    {
        var expected = new HashSet<string> { "contacts:conversation:handle", "contacts:contact:view" };
        _userRoleStore.GetEffectivePermissionsAsync(_tenantId, _userId, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.ResolveAsync(_tenantId, _userId, CancellationToken.None);

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnCachedResult_WhenCalledTwice()
    {
        var expected = new HashSet<string> { "contacts:conversation:handle" };
        _userRoleStore.GetEffectivePermissionsAsync(_tenantId, _userId, Arg.Any<CancellationToken>())
            .Returns(expected);

        await _sut.ResolveAsync(_tenantId, _userId, CancellationToken.None);
        await _sut.ResolveAsync(_tenantId, _userId, CancellationToken.None);

        await _userRoleStore.Received(1)
            .GetEffectivePermissionsAsync(_tenantId, _userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ShouldRefreshCache_WhenInvalidated()
    {
        var first = new HashSet<string> { "contacts:conversation:handle" };
        var second = new HashSet<string> { "contacts:conversation:handle", "queues:queue:view" };
        _userRoleStore.GetEffectivePermissionsAsync(_tenantId, _userId, Arg.Any<CancellationToken>())
            .Returns(first, second);

        await _sut.ResolveAsync(_tenantId, _userId, CancellationToken.None);
        _sut.InvalidateUser(_tenantId, _userId);
        var result = await _sut.ResolveAsync(_tenantId, _userId, CancellationToken.None);

        result.Should().BeEquivalentTo(second);
        await _userRoleStore.Received(2)
            .GetEffectivePermissionsAsync(_tenantId, _userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void HasPermission_ShouldReturnTrue_WhenPermissionExists()
    {
        var permissions = new HashSet<string> { "contacts:conversation:handle", "contacts:contact:view" };

        PermissionResolver.HasPermission(permissions, "contacts:contact:view").Should().BeTrue();
    }

    [Fact]
    public void HasPermission_ShouldReturnFalse_WhenPermissionMissing()
    {
        var permissions = new HashSet<string> { "contacts:conversation:handle" };

        PermissionResolver.HasPermission(permissions, "queues:queue:delete").Should().BeFalse();
    }

    [Fact]
    public async Task InvalidateTenant_ShouldClearAllUsersInTenant()
    {
        var perms = new HashSet<string> { "contacts:conversation:handle" };
        var user2 = EntityId.From("user-2");
        _userRoleStore.GetEffectivePermissionsAsync(_tenantId, Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(perms);

        await _sut.ResolveAsync(_tenantId, _userId, CancellationToken.None);
        await _sut.ResolveAsync(_tenantId, user2, CancellationToken.None);
        _sut.InvalidateTenant(_tenantId);
        await _sut.ResolveAsync(_tenantId, _userId, CancellationToken.None);
        await _sut.ResolveAsync(_tenantId, user2, CancellationToken.None);

        await _userRoleStore.Received(4)
            .GetEffectivePermissionsAsync(_tenantId, Arg.Any<EntityId>(), Arg.Any<CancellationToken>());
    }

    // contact-history-opens-by-assignment task 1.8 (design D16, ADR-0037 exact membership): read never satisfies a
    // monitor (listen) or handle gate, and monitor never satisfies a read gate.
    [Theory]
    [InlineData("contacts:conversation:monitor")]
    [InlineData("contacts:conversation:handle")]
    [InlineData("contacts:conversation:barge")]
    [InlineData("contacts:conversation:whisper")]
    public void HasPermission_ShouldBeFalse_WhenTheRoleHoldsOnlyTheReadPermission(string gate)
    {
        var onlyRead = new HashSet<string>(StringComparer.Ordinal) { "contacts:conversation:read" };

        PermissionResolver.HasPermission(onlyRead, gate).Should().BeFalse();
    }

    [Fact]
    public void HasPermission_ShouldBeFalseForRead_WhenTheRoleHoldsOnlyMonitor()
    {
        var onlyMonitor = new HashSet<string>(StringComparer.Ordinal) { "contacts:conversation:monitor" };

        PermissionResolver.HasPermission(onlyMonitor, "contacts:conversation:read").Should().BeFalse();
    }
}
