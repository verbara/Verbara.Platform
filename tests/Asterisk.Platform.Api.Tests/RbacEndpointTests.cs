using System.Net;
using System.Net.Http.Json;

namespace Asterisk.Platform.Api.Tests;

public sealed class RbacEndpointTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public RbacEndpointTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GetPermissions_ShouldReturn200_WhenAuthenticated()
    {
        var response = await _client.GetAsync("/api/admin/permissions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPermissionCategories_ShouldReturnGroupedPermissions()
    {
        var response = await _client.GetAsync("/api/admin/permissions/categories");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetRoleTemplates_ShouldReturn200_WhenAuthenticated()
    {
        var response = await _client.GetAsync("/api/admin/role-templates");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetRoleTemplate_ShouldReturn404_WhenNotFound()
    {
        var response = await _client.GetAsync("/api/admin/role-templates/nonexistent");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListTenantRoles_ShouldReturn200()
    {
        var response = await _client.GetAsync("/api/admin/roles");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateTenantRole_ShouldReturn201_WhenValidRequest()
    {
        var request = new { Name = "TestRole", Description = "Test role for integration tests" };
        var response = await _client.PostAsJsonAsync("/api/admin/roles", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task DeleteTenantRole_ShouldReturn204_WhenCustomRoleWithNoUsers()
    {
        // Create a custom role first
        var createRequest = new { Name = "ToDelete", Description = "Will be deleted" };
        var createResponse = await _client.PostAsJsonAsync("/api/admin/roles", createRequest);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<RoleResponse>();

        var response = await _client.DeleteAsync($"/api/admin/roles/{created!.RoleId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task GetUserRoles_ShouldReturn200()
    {
        var response = await _client.GetAsync("/api/admin/users/user-1/roles");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetUserPermissions_ShouldReturn200()
    {
        var response = await _client.GetAsync("/api/admin/users/user-1/permissions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AddUserRole_ShouldReturn204()
    {
        // First create a role to assign
        var createRequest = new { Name = "ForAssignment" };
        var createResponse = await _client.PostAsJsonAsync("/api/admin/roles", createRequest);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<RoleResponse>();

        var response = await _client.PostAsync(
            $"/api/admin/users/user-1/roles/{created!.RoleId}", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RemoveUserRole_ShouldReturn204()
    {
        // Create and assign a role, then remove it
        var createRequest = new { Name = "ForRemoval" };
        var createResponse = await _client.PostAsJsonAsync("/api/admin/roles", createRequest);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<RoleResponse>();

        await _client.PostAsync($"/api/admin/users/user-1/roles/{created!.RoleId}", null);

        var response = await _client.DeleteAsync(
            $"/api/admin/users/user-1/roles/{created.RoleId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ReplaceUserRoles_ShouldReturn200()
    {
        var request = new { RoleIds = new[] { "agent" } };
        var response = await _client.PutAsJsonAsync("/api/admin/users/user-1/roles", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- Auth Tests ---

    [Fact]
    public async Task GetPermissions_ShouldReturn401_WhenNoAuth()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/permissions");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record RoleResponse(string RoleId, string Name, string? Description);
}
