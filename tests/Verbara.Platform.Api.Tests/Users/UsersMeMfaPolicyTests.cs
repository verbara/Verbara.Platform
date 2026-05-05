using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Users;

/// <summary>
/// R5.2 E.2 — verify <c>/users/me</c> returns the new <c>mfaPolicy</c> snapshot
/// (Enforced + PolicySource) so the Web can hide "Disable MFA" proactively
/// instead of relying on a 403 round-trip from the existing policy enforcer.
/// Pairs with the same gating wired in <c>MfaPolicyEnforcementTests</c>.
/// </summary>
public sealed class UsersMeMfaPolicyTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public UsersMeMfaPolicyTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GetUsersMe_ShouldIncludeMfaPolicy_WhenEnforced()
    {
        // Seed a tenant policy that requires MFA for all roles. The factory
        // pre-seeds an Admin user under the test tenant.
        using var scope = _factory.Services.CreateScope();
        var configStore = scope.ServiceProvider.GetRequiredService<ITenantAuthConfigStore>();
        await configStore.SaveAsync(new TenantAuthConfig
        {
            TenantId = AuthenticatedPlatformApiFactory.TestTenantId,
            MfaPolicy = "required_all",
        }, CancellationToken.None);

        var response = await _client.GetAsync("/api/v1/users/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<MeDto>(s_jsonOptions);
        dto.Should().NotBeNull();
        dto!.MfaPolicy.Should().NotBeNull();
        dto.MfaPolicy!.Enforced.Should().BeTrue();
        dto.MfaPolicy.PolicySource.Should().Be("tenant");
    }

    [Fact]
    public async Task GetUsersMe_ShouldIncludeMfaPolicy_WhenNotEnforced()
    {
        using var scope = _factory.Services.CreateScope();
        var configStore = scope.ServiceProvider.GetRequiredService<ITenantAuthConfigStore>();
        await configStore.SaveAsync(new TenantAuthConfig
        {
            TenantId = AuthenticatedPlatformApiFactory.TestTenantId,
            MfaPolicy = "optional",
        }, CancellationToken.None);

        var response = await _client.GetAsync("/api/v1/users/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<MeDto>(s_jsonOptions);
        dto.Should().NotBeNull();
        dto!.MfaPolicy.Should().NotBeNull();
        dto.MfaPolicy!.Enforced.Should().BeFalse();
        dto.MfaPolicy.PolicySource.Should().Be("user");
    }

    private sealed record MeDto(string UserId, string Email, MfaPolicySnapshot? MfaPolicy);

    private sealed record MfaPolicySnapshot(bool Enforced, string PolicySource);
}
