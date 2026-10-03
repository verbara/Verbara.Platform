using Verbara.Platform.Core;
using Verbara.Platform.Identity.OidcTokenExchange;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Identity.Tests;

public sealed class OidcUserProvisioningServiceTests
{
    private readonly InMemoryUserStore _userStore = new();
    private readonly InMemoryTenantRoleStore _tenantRoles = new();
    private readonly InMemoryUserRoleStore _userRoles = new();
    private readonly IRoleTemplateStore _roleTemplates = Substitute.For<IRoleTemplateStore>();
    private readonly OidcUserProvisioningService _sut;

    public OidcUserProvisioningServiceTests()
    {
        _sut = new OidcUserProvisioningService(
            _userStore,
            _tenantRoles,
            _roleTemplates,
            _userRoles,
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
        await _userStore.CreateAsync(existing, CancellationToken.None);

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
        await _userStore.CreateAsync(existing, CancellationToken.None);

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

    // ─── Only the IdP-vouched profile fields are written ─────────────────────

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldWriteOnlyTheProfileAndLastLogin_WhenTheSubjectMatches()
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
            PasswordHash = "stored-hash",
            FailedLoginAttempts = 2,
        };
        await _userStore.CreateAsync(existing, CancellationToken.None);

        await _sut.ProvisionOrUpdateAsync(
            "tenant-1", new OidcClaimsResult("oidc-sub-1", "user@example.com", "New Name", true), DefaultConfig(), CancellationToken.None);

        var stored = (await _userStore.GetByIdAsync(existing.TenantId, existing.UserId, CancellationToken.None))!;
        stored.DisplayName.Should().Be("New Name");
        stored.EmailVerified.Should().BeTrue();
        stored.LastLoginAt.Should().NotBeNull();
        stored.Role.Should().Be(UserRole.Supervisor);
        stored.PasswordHash.Should().Be("stored-hash", because: "a sign-in writes no credential column");
        stored.FailedLoginAttempts.Should().Be(2, because: "a sign-in writes no lockout column");
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldLinkWithoutTouchingMfaOrRole_WhenMatchedByEmail()
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
            MfaEnabled = true,
            MfaSecret = "SECRET",
            MfaRecoveryCodes = ["digest"],
        };
        await _userStore.CreateAsync(existing, CancellationToken.None);

        await _sut.ProvisionOrUpdateAsync(
            "tenant-1", new OidcClaimsResult("oidc-sub-new", "user@example.com", "Admin User", true), DefaultConfig(), CancellationToken.None);

        var stored = (await _userStore.GetByIdAsync(existing.TenantId, existing.UserId, CancellationToken.None))!;
        stored.OidcSubject.Should().Be("oidc-sub-new");
        stored.AuthProvider.Should().Be("oidc");
        stored.EmailVerified.Should().BeTrue();
        stored.Role.Should().Be(UserRole.Admin);
        stored.MfaEnabled.Should().BeTrue();
        stored.MfaSecret.Should().Be("SECRET");
        stored.MfaRecoveryCodes.Should().Equal("digest");
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldReturnNullAndCreateNothing_WhenTheMatchedUserIsDeletedBeforeTheWrite()
    {
        // The subject matched a user that was deleted before this sign-in wrote to it: the writes are
        // update-only, so the user stays deleted and the sign-in is refused.
        var store = Substitute.For<IUserStore>();
        var matched = new User
        {
            UserId = EntityId.New(),
            TenantId = new TenantId("tenant-1"),
            Email = "user@example.com",
            DisplayName = "Old Name",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            OidcSubject = "oidc-sub-1",
        };
        store.FindByOidcSubjectAsync(Arg.Any<TenantId>(), "oidc-sub-1", Arg.Any<CancellationToken>()).Returns(matched);
        var sut = new OidcUserProvisioningService(
            store, _tenantRoles, _roleTemplates, _userRoles, NullLogger<OidcUserProvisioningService>.Instance);

        var user = await sut.ProvisionOrUpdateAsync(
            "tenant-1", new OidcClaimsResult("oidc-sub-1", "user@example.com", "New Name", true), DefaultConfig(), CancellationToken.None);

        user.Should().BeNull();
        await store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    // ─── A provisioned user holds its role's permissions from the first sign-in ─

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldGrantTheTenantRoleOfItsRole_WhenItCreatesTheUser()
    {
        // A tenant provisioned with its own role ids: its Supervisor role is found by the template's name.
        _roleTemplates.GetByIdAsync("supervisor", Arg.Any<CancellationToken>()).Returns(new RoleTemplate
        {
            TemplateId = "supervisor",
            Name = "Supervisor",
            Description = "Team supervisor",
            IsSystem = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _tenantRoles.SaveAsync(TenantRoleOf("role_supervisor_tenant-1", "Supervisor", "supervisor"), CancellationToken.None);
        var config = new TenantAuthConfig
        {
            TenantId = "tenant-1",
            OidcAutoCreateUsers = true,
            OidcDefaultRole = "Supervisor",
        };

        var user = await _sut.ProvisionOrUpdateAsync(
            "tenant-1", new OidcClaimsResult("oidc-sub-1", "sup@example.com", "Sup", true), config, CancellationToken.None);

        user.Should().NotBeNull();
        var grants = await _userRoles.GetRolesForUserAsync(new TenantId("tenant-1"), user!.UserId, CancellationToken.None);
        grants.Should().ContainSingle().Which.RoleId.Should().Be("role_supervisor_tenant-1");
        grants[0].AssignedBy.Should().Be(DefaultTenantRole.AssignedBy);
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldGrantNothingNew_WhenTheSubjectMatchesAnExistingUser()
    {
        await _tenantRoles.SaveAsync(TenantRoleOf("agent", "Agent", "agent"), CancellationToken.None);
        var existing = new User
        {
            UserId = EntityId.New(),
            TenantId = new TenantId("tenant-1"),
            Email = "user@example.com",
            DisplayName = "User",
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            AuthProvider = "oidc",
            OidcSubject = "oidc-sub-1",
        };
        await _userStore.CreateAsync(existing, CancellationToken.None);

        await _sut.ProvisionOrUpdateAsync(
            "tenant-1", new OidcClaimsResult("oidc-sub-1", "user@example.com", "User", true), DefaultConfig(), CancellationToken.None);

        (await _userRoles.GetRolesForUserAsync(existing.TenantId, existing.UserId, CancellationToken.None)).Should().BeEmpty(
            because: "only a user this sign-in creates is granted a role; an existing user's grants are its own");
    }

    [Fact]
    public async Task ProvisionOrUpdateAsync_ShouldStillReturnTheCreatedUser_WhenGrantingItsRoleFails()
    {
        var failingRoles = Substitute.For<IUserRoleStore>();
        failingRoles.AssignAsync(default, default, default!, default, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("role store unavailable")));
        await _tenantRoles.SaveAsync(TenantRoleOf("agent", "Agent", "agent"), CancellationToken.None);
        var sut = new OidcUserProvisioningService(
            _userStore, _tenantRoles, _roleTemplates, failingRoles, NullLogger<OidcUserProvisioningService>.Instance);

        var user = await sut.ProvisionOrUpdateAsync(
            "tenant-1", new OidcClaimsResult("oidc-sub-1", "user@example.com", "User", true), DefaultConfig(), CancellationToken.None);

        user.Should().NotBeNull(because: "the user is created; the next start's role migration grants what this sign-in could not");
        (await _userStore.GetByIdAsync(new TenantId("tenant-1"), user!.UserId, CancellationToken.None)).Should().NotBeNull();
    }

    private static TenantRole TenantRoleOf(string roleId, string name, string templateId) => new()
    {
        RoleId = roleId,
        TenantId = new TenantId("tenant-1"),
        Name = name,
        SourceTemplateId = templateId,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
