using Verbara.Platform.Identity.Mfa;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Verbara.Platform.Identity.Tests.Mfa;

public sealed class TenantAuthConfigMfaPolicyEvaluatorTests
{
    private const string TenantId = "evaluator-test-tenant";

    // ─── Test 1: null config (no tenant entry) → false ───────────────────────

    [Fact]
    public async Task RequiresMfaAsync_ShouldReturnFalse_WhenTenantConfigMissing()
    {
        var store = Substitute.For<ITenantAuthConfigStore>();
        store.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(null));

        var sut = new TenantAuthConfigMfaPolicyEvaluator(store);

        var result = await sut.RequiresMfaAsync(TenantId, UserRole.Admin, CancellationToken.None);

        result.Should().BeFalse(
            because: "a missing tenant config defaults to optional MFA policy");
    }

    // ─── Test 2: required_all policy + Admin role → true ─────────────────────

    [Fact]
    public async Task RequiresMfaAsync_ShouldReturnTrue_WhenPolicyRequiresMfaForRole()
    {
        var config = new TenantAuthConfig
        {
            TenantId = TenantId,
            MfaPolicy = "required_all",
        };
        var store = Substitute.For<ITenantAuthConfigStore>();
        store.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(config));

        var sut = new TenantAuthConfigMfaPolicyEvaluator(store);

        var result = await sut.RequiresMfaAsync(TenantId, UserRole.Admin, CancellationToken.None);

        result.Should().BeTrue(
            because: "required_all policy mandates MFA for every role");
    }

    // ─── Test 3: optional policy → false for any role ─────────────────────────

    [Fact]
    public async Task RequiresMfaAsync_ShouldReturnFalse_WhenPolicyOptionalForRole()
    {
        var config = new TenantAuthConfig
        {
            TenantId = TenantId,
            MfaPolicy = "optional",
        };
        var store = Substitute.For<ITenantAuthConfigStore>();
        store.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantAuthConfig?>(config));

        var sut = new TenantAuthConfigMfaPolicyEvaluator(store);

        var result = await sut.RequiresMfaAsync(TenantId, UserRole.Admin, CancellationToken.None);

        result.Should().BeFalse(
            because: "optional policy never mandates MFA regardless of role");
    }
}
