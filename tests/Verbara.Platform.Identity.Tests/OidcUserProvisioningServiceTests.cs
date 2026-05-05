using Verbara.Platform.Core;
using Verbara.Platform.Identity.OidcTokenExchange;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Platform.Identity.Tests;

public sealed class OidcUserProvisioningServiceTests
{
    private readonly InMemoryUserStore _userStore = new();
    private readonly OidcUserProvisioningService _sut;

    public OidcUserProvisioningServiceTests()
    {
        _sut = new OidcUserProvisioningService(
            _userStore,
            NullLogger<OidcUserProvisioningService>.Instance);
    }

    private static TenantAuthConfig DefaultConfig(bool autoCreate = true) => new()
    {
        TenantId = "tenant-1",
        OidcEnabled = true,
        OidcAutoCreateUsers = autoCreate,
        OidcDefaultRole = "Agent",
    };

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldCreateNewUser_WhenAutoCreateEnabled()
    {
        var claims = new OidcClaimsResult("oidc-sub-1", "user@example.com", "Test User", true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, DefaultConfig(), CancellationToken.None);

        user.Should().NotBeNull();
        user!.Email.Should().Be("user@example.com");
        user.DisplayName.Should().Be("Test User");
        user.OidcSubject.Should().Be("oidc-sub-1");
        user.AuthProvider.Should().Be("oidc");
        user.Role.Should().Be(UserRole.Agent);
        user.Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldReturnNull_WhenAutoCreateDisabled()
    {
        var claims = new OidcClaimsResult("oidc-sub-1", "user@example.com", "Test User", true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, DefaultConfig(autoCreate: false), CancellationToken.None);

        user.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldReturnExistingUser_WhenOidcSubjectMatches()
    {
        var existing = new User
        {
            UserId = EntityId.New(),
            TenantId = new TenantId("tenant-1"),
            Email = "user@example.com",
            DisplayName = "Old Name",
            Role = UserRole.Supervisor,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            AuthProvider = "oidc",
            OidcSubject = "oidc-sub-1",
        };
        await _userStore.SaveAsync(existing, CancellationToken.None);

        var claims = new OidcClaimsResult("oidc-sub-1", "user@example.com", "New Name", true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, DefaultConfig(), CancellationToken.None);

        user.Should().NotBeNull();
        user!.UserId.Should().Be(existing.UserId);
        user.DisplayName.Should().Be("New Name"); // Updated
        user.Role.Should().Be(UserRole.Supervisor); // Preserved, not overwritten
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldLinkByEmail_WhenOidcSubjectNotFoundButEmailMatches()
    {
        var existing = new User
        {
            UserId = EntityId.New(),
            TenantId = new TenantId("tenant-1"),
            Email = "user@example.com",
            DisplayName = "Admin User",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            AuthProvider = "local",
        };
        await _userStore.SaveAsync(existing, CancellationToken.None);

        var claims = new OidcClaimsResult("oidc-sub-new", "user@example.com", "Admin User", true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, DefaultConfig(), CancellationToken.None);

        user.Should().NotBeNull();
        user!.UserId.Should().Be(existing.UserId);
        user.OidcSubject.Should().Be("oidc-sub-new"); // Linked
        user.AuthProvider.Should().Be("oidc"); // Updated
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldUseDefaultRole_WhenConfigured()
    {
        var config = new TenantAuthConfig
        {
            TenantId = "tenant-1",
            OidcAutoCreateUsers = true,
            OidcDefaultRole = "Supervisor",
        };

        var claims = new OidcClaimsResult("oidc-sub-1", "supervisor@example.com", "Sup User", true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, config, CancellationToken.None);

        user.Should().NotBeNull();
        user!.Role.Should().Be(UserRole.Supervisor);
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldFallbackToAgent_WhenRoleInvalid()
    {
        var config = new TenantAuthConfig
        {
            TenantId = "tenant-1",
            OidcAutoCreateUsers = true,
            OidcDefaultRole = "InvalidRole",
        };

        var claims = new OidcClaimsResult("oidc-sub-1", "agent@example.com", "Agent", true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, config, CancellationToken.None);

        user.Should().NotBeNull();
        user!.Role.Should().Be(UserRole.Agent);
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldUseEmail_WhenNameIsNull()
    {
        var claims = new OidcClaimsResult("oidc-sub-1", "user@example.com", null, true);

        var user = await _sut.ProvisionOrUpdateAsync("tenant-1", claims, DefaultConfig(), CancellationToken.None);

        user.Should().NotBeNull();
        user!.DisplayName.Should().Be("user@example.com");
    }
}
