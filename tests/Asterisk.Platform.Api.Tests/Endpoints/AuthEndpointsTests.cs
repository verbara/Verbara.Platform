using System.Security.Claims;
using System.Text.Json;
using Asterisk.Platform.Api.Endpoints;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests.Endpoints;

public sealed class AuthEndpointsTests
{
    private const string TestTenantId = "tenant1";
    private const string TestUserId = "user1";
    private const string TestPassword = "CurrentPassword123!";

    // BCrypt hash of TestPassword — computed once at class load to avoid rehashing for every test.
    private static readonly string s_knownHash = PasswordService.HashPassword(TestPassword);

    [Fact]
    public void MfaChallengeResponse_ShouldSerializeWithFrontendFieldNames_WhenSerialized()
    {
        var response = new MfaChallengeResponse(true, "abc123");

        var json = JsonSerializer.Serialize(response, ApiJsonContext.Default.MfaChallengeResponse);

        json.Should().Contain("\"requiresMfa\":true");
        json.Should().Contain("\"mfaToken\":\"abc123\"");
    }

    [Fact]
    public async Task MfaDisable_ShouldReturn403_WhenTenantPolicyRequiresMfaAll()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var authEventService = new AuthEventService(authEventStore);

        var user = BuildUser(mfaEnabled: true, role: UserRole.Agent);
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        var authConfig = new TenantAuthConfig
        {
            TenantId = TestTenantId,
            MfaPolicy = "required_all",
        };
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(authConfig));

        var request = new MfaDisableRequest(TestPassword);
        var result = await AuthEndpoints.MfaDisable(
            request, BuildHttpContext(), userStore, tenantAuthConfigStore,
            authEventService, CancellationToken.None);

        var statusResult = result.Should().BeOfType<JsonHttpResult<ErrorResponse>>().Subject;
        statusResult.StatusCode.Should().Be(403);
        user.MfaEnabled.Should().BeTrue("policy should block disable and leave MFA enabled");
    }

    [Fact]
    public async Task MfaDisable_ShouldReturn403_WhenUserRoleIsInMfaRequiredRoles()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var authEventService = new AuthEventService(authEventStore);

        var user = BuildUser(mfaEnabled: true, role: UserRole.Admin);
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        var authConfig = new TenantAuthConfig
        {
            TenantId = TestTenantId,
            MfaPolicy = "required_for_roles",
            MfaRequiredRoles = new[] { "Admin", "Supervisor" },
        };
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(authConfig));

        var request = new MfaDisableRequest(TestPassword);
        var result = await AuthEndpoints.MfaDisable(
            request, BuildHttpContext(), userStore, tenantAuthConfigStore,
            authEventService, CancellationToken.None);

        var statusResult = result.Should().BeOfType<JsonHttpResult<ErrorResponse>>().Subject;
        statusResult.StatusCode.Should().Be(403);
        user.MfaEnabled.Should().BeTrue("policy should block disable and leave MFA enabled");
    }

    [Fact]
    public async Task MfaDisable_ShouldSucceed_WhenTenantPolicyIsOptional()
    {
        var userStore = Substitute.For<IUserStore>();
        var tenantAuthConfigStore = Substitute.For<ITenantAuthConfigStore>();
        var authEventStore = Substitute.For<IAuthEventStore>();
        var authEventService = new AuthEventService(authEventStore);

        var user = BuildUser(mfaEnabled: true, role: UserRole.Agent);
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<User?>(user));

        var authConfig = new TenantAuthConfig
        {
            TenantId = TestTenantId,
            MfaPolicy = "optional",
        };
        tenantAuthConfigStore.GetAsync(TestTenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(authConfig));

        var request = new MfaDisableRequest(TestPassword);
        var result = await AuthEndpoints.MfaDisable(
            request, BuildHttpContext(), userStore, tenantAuthConfigStore,
            authEventService, CancellationToken.None);

        result.Should().BeOfType<Ok<MessageResponse>>();
        user.MfaEnabled.Should().BeFalse();
        user.MfaSecret.Should().BeNull();
        user.MfaRecoveryCodes.Should().BeNull();
        user.MfaConfirmedAt.Should().BeNull();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static User BuildUser(bool mfaEnabled = false, UserRole role = UserRole.Agent)
    {
        return new User
        {
            UserId = EntityId.From(TestUserId),
            TenantId = new TenantId(TestTenantId),
            Email = "test@example.com",
            DisplayName = "Test User",
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            MfaEnabled = mfaEnabled,
            MfaSecret = mfaEnabled ? "SECRET" : null,
            MfaRecoveryCodes = mfaEnabled ? new[] { "code1", "code2" } : null,
            MfaConfirmedAt = mfaEnabled ? DateTimeOffset.UtcNow : null,
            PasswordHash = s_knownHash,
        };
    }

    private static DefaultHttpContext BuildHttpContext()
    {
        var ctx = new DefaultHttpContext();
        var claims = new List<Claim>
        {
            new("tid", TestTenantId),
            new("sub", TestUserId),
            new("user_id", TestUserId),
        };
        var identity = new ClaimsIdentity(claims, "TestScheme");
        ctx.User = new ClaimsPrincipal(identity);
        return ctx;
    }
}
