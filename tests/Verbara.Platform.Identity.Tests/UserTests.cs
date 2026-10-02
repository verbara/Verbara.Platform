using Verbara.Platform.Core;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Identity.Tests;

public class UserTests
{
    [Fact]
    public void Constructor_ShouldCreateUser_WhenValidInput()
    {
        var user = new User
        {
            UserId = EntityId.From("u-001"),
            TenantId = new TenantId("t1"),
            Email = "agent@example.com",
            DisplayName = "Agent Smith",
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        user.Email.Should().Be("agent@example.com");
        user.Role.Should().Be(UserRole.Agent);
        user.TenantId.Value.Should().Be("t1");
    }

    [Fact]
    public void HasPermission_ShouldReturnTrue_WhenAdminRole()
    {
        var user = new User
        {
            UserId = EntityId.From("u-001"),
            TenantId = new TenantId("t1"),
            Email = "admin@example.com",
            DisplayName = "Admin",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        user.HasPermission(Permission.ManageUsers).Should().BeTrue();
        user.HasPermission(Permission.ManageQueues).Should().BeTrue();
    }

    [Fact]
    public void HasPermission_ShouldReturnFalse_WhenAgentLacksPermission()
    {
        var user = new User
        {
            UserId = EntityId.From("u-001"),
            TenantId = new TenantId("t1"),
            Email = "agent@example.com",
            DisplayName = "Agent",
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        user.HasPermission(Permission.ManageUsers).Should().BeFalse();
        user.HasPermission(Permission.HandleConversations).Should().BeTrue();
    }

    [Fact]
    public void HasPermission_ShouldReturnFalse_WhenUserInactive()
    {
        var user = new User
        {
            UserId = EntityId.From("u-001"),
            TenantId = new TenantId("t1"),
            Email = "agent@example.com",
            DisplayName = "Agent",
            Role = UserRole.Admin,
            Status = UserStatus.Suspended,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        user.HasPermission(Permission.ManageUsers).Should().BeFalse();
    }

    // ─── CanAuthenticate: the one account-status rule every auth path applies ──

    [Fact]
    public void CanAuthenticate_ShouldBeTrue_WhenStatusIsActive()
    {
        UserWith(UserStatus.Active).CanAuthenticate.Should().BeTrue();
    }

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public void CanAuthenticate_ShouldBeFalse_WhenStatusIsNotActive(UserStatus status)
    {
        UserWith(status).CanAuthenticate.Should().BeFalse();
    }

    [Fact]
    public void CanAuthenticate_ShouldAdmitOnlyActive_WhenCheckedAcrossEveryDefinedStatus()
    {
        // Allow-list, not deny-list: a status added to the enum later is refused everywhere until
        // someone deliberately decides otherwise here.
        foreach (var status in Enum.GetValues<UserStatus>())
        {
            UserWith(status).CanAuthenticate.Should().Be(status == UserStatus.Active,
                because: $"only Active may authenticate, and {status} is {(status == UserStatus.Active ? "" : "not ")}Active");
        }
    }

    [Fact]
    public void CanAuthenticate_ShouldBeFalse_WhenStatusIsUndefinedValue()
    {
        UserWith((UserStatus)99).CanAuthenticate.Should().BeFalse();
    }

    private static User UserWith(UserStatus status) => new()
    {
        UserId = EntityId.From("u-status"),
        TenantId = new TenantId("t1"),
        Email = "status@example.com",
        DisplayName = "Status",
        Role = UserRole.Agent,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
