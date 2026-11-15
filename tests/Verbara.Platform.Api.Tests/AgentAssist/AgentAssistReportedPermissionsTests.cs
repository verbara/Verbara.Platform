using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Auth;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// Sign-in and token refresh report <c>features:agent-assist:manage</c> only to a user of the Platform tenant: only
/// the Platform tenant passes that toggle's gate, and the reported list drives the console's navigation. Each case
/// goes through <c>POST /api/v1/auth/login</c> or <c>POST /api/v1/auth/refresh</c> on the real host, whose
/// <see cref="IUserRoleStore"/> answers an Admin-role user with the built-in <c>admin</c> template's permissions,
/// which hold both Agent Assist ids.
/// </summary>
public sealed class AgentAssistReportedPermissionsTests : IClassFixture<AgentAssistTenantScopeApiFactory>
{
    private const string Password = "Reported-Perms-Passw0rd!";
    private const string FeatureManage = PlatformAdminPermissions.AgentAssistFeatureManage;
    private const string ConfigManage = PlatformAdminPermissions.AgentAssistConfigManage;

    private readonly AgentAssistTenantScopeApiFactory _factory;

    public AgentAssistReportedPermissionsTests(AgentAssistTenantScopeApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_ShouldNotReportAgentAssistFeatureManage_WhenUserIsCustomerTenantAdmin()
    {
        var user = NewAdmin(AgentAssistTenantScopeApiFactory.Acme);

        var permissions = await LoginPermissionsAsync(user);

        permissions.Should().NotContain(FeatureManage, "a Customer tenant never passes the toggle's gate");
        permissions.Should().Contain(ConfigManage, "only the toggle's id is held back from the reported list");
    }

    [Fact]
    public async Task Login_ShouldNotReportAgentAssistFeatureManage_WhenUserIsPartnerTenantAdmin()
    {
        var user = NewAdmin(NewPartnerTenant());

        var permissions = await LoginPermissionsAsync(user);

        permissions.Should().NotContain(FeatureManage, "a Partner tenant never passes the toggle's gate");
    }

    [Fact]
    public async Task Login_ShouldReportAgentAssistFeatureManage_WhenUserIsPlatformTenantAdmin()
    {
        var user = NewAdmin(AccountStatusApiFactory.PlatformTenantId);

        var permissions = await LoginPermissionsAsync(user);

        permissions.Should().Contain(FeatureManage).And.Contain(ConfigManage);
    }

    [Fact]
    public async Task Refresh_ShouldNotReportAgentAssistFeatureManage_WhenUserIsCustomerTenantAdmin()
    {
        var user = NewAdmin(AgentAssistTenantScopeApiFactory.Acme);

        var permissions = await RefreshPermissionsAsync(user);

        permissions.Should().NotContain(FeatureManage, "a Customer tenant never passes the toggle's gate");
        permissions.Should().Contain(ConfigManage);
    }

    [Fact]
    public async Task Refresh_ShouldReportAgentAssistFeatureManage_WhenUserIsPlatformTenantAdmin()
    {
        var user = NewAdmin(AccountStatusApiFactory.PlatformTenantId);

        var permissions = await RefreshPermissionsAsync(user);

        permissions.Should().Contain(FeatureManage).And.Contain(ConfigManage);
    }

    private User NewAdmin(string tenantId) =>
        _factory.SaveUser($"{tenantId}-rp-admin-{Guid.NewGuid():N}", tenantId, UserRole.Admin, UserStatus.Active, Password);

    private string NewPartnerTenant()
    {
        var tenantId = $"rp-partner-{Guid.NewGuid().ToString("N")[..12]}";
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantStore>().UpsertAsync(new Tenant
        {
            TenantId = tenantId,
            Name = tenantId,
            Status = TenantStatus.Active,
            Type = TenantType.Partner,
            ParentTenantId = AccountStatusApiFactory.PlatformTenantId,
        }).AsTask().GetAwaiter().GetResult();
        return tenantId;
    }

    private async Task<string[]> LoginPermissionsAsync(User user)
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { tenantId = user.TenantId.Value, email = user.Email, password = Password });
        return await PermissionsOfAsync(response);
    }

    private async Task<string[]> RefreshPermissionsAsync(User user)
    {
        var (rawToken, _) = await _factory.Services.GetRequiredService<RefreshTokenService>()
            .GenerateAsync(user.UserId.Value, user.TenantId.Value, "10.0.0.1", "test", CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Cookie", $"{RefreshTokenCookie.Name}={rawToken}");
        using var client = _factory.CreateClient();
        using var response = await client.SendAsync(request);
        return await PermissionsOfAsync(response);
    }

    private static async Task<string[]> PermissionsOfAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var doc = JsonDocument.Parse(text);
        return [.. doc.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];
    }
}
