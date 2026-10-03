using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Api.Tests.Logging;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// <c>POST /admin/users</c> grants the new user the tenant role its <see cref="UserRole"/> maps to, in
/// the same request, so its server-side permissions do not wait for the role migration that runs at
/// the next start. The host's role stores are the real in-memory ones.
/// </summary>
public sealed class CreateUserDefaultRoleTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly TenantId s_tenant = new(AuthenticatedPlatformApiFactory.TestTenantId);

    private readonly AuthenticatedPlatformApiFactory _factory;

    public CreateUserDefaultRoleTests(AuthenticatedPlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateUser_ShouldGrantTheTenantRoleOfItsRole_WhenTheTenantHasIt()
    {
        await SaveTenantRoleAsync("agent", "Agent");
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/v1/admin/users", new
        {
            email = $"new-agent-{Guid.NewGuid():N}@example.com",
            displayName = "New Agent",
            role = "Agent",
            password = "Sup3r-Secret-Passw0rd!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var grants = await GrantsOfAsync(await CreatedUserIdAsync(response));
        grants.Should().ContainSingle().Which.RoleId.Should().Be("agent");
        grants[0].AssignedBy.Should().Be(DefaultTenantRole.AssignedBy);
    }

    [Fact]
    public async Task CreateUser_ShouldCreateTheUserWithoutAGrant_WhenTheTenantHasNoRoleForItsRole()
    {
        // No "api" role in the tenant and no template to clone it from: the user is still created, and
        // the next start's role migration grants it.
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/v1/admin/users", new
        {
            email = $"new-integration-{Guid.NewGuid():N}@example.com",
            displayName = "New Integration",
            role = "Api",
            password = "Sup3r-Secret-Passw0rd!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        (await GrantsOfAsync(await CreatedUserIdAsync(response))).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateUser_ShouldCreateTheUserAndLogAWarning_WhenGrantingItsRoleFails()
    {
        await using var factory = new FailingRoleGrantApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/v1/admin/users", new
        {
            email = $"new-agent-{Guid.NewGuid():N}@example.com",
            displayName = "New Agent",
            role = "Agent",
            password = "Sup3r-Secret-Passw0rd!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        factory.Logs.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning && r.EventId.Id == 7501);
    }

    private async Task SaveTenantRoleAsync(string roleId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<ITenantRoleStore>();
        if (await roles.GetByIdAsync(s_tenant, roleId, CancellationToken.None) is not null)
            return;
        await roles.SaveAsync(
            new TenantRole
            {
                RoleId = roleId,
                TenantId = s_tenant,
                Name = name,
                SourceTemplateId = roleId,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
    }

    private async Task<IReadOnlyList<UserRoleAssignment>> GrantsOfAsync(EntityId userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUserRoleStore>()
            .GetRolesForUserAsync(s_tenant, userId, CancellationToken.None);
    }

    private static async Task<EntityId> CreatedUserIdAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return EntityId.From(json.RootElement.GetProperty("id").GetString()!);
    }
}

/// <summary>A host whose role store refuses every grant, recording what the host logs.</summary>
internal sealed class FailingRoleGrantApiFactory : AuthenticatedPlatformApiFactory
{
    public LogRecordCapture Logs { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUserRoleStore>();
            var roles = Substitute.For<IUserRoleStore>();
            roles.AssignAsync(default, default, default!, default, default)
                .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("role store unavailable")));
            services.AddSingleton(roles);
            services.AddSingleton<ITenantRoleStore>(new SingleAgentRoleStore());
        });
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        return base.CreateHost(builder);
    }

    // The tenant has its Agent role, so the grant gets as far as the failing write.
    private sealed class SingleAgentRoleStore : ITenantRoleStore
    {
        private static readonly TenantRole s_agent = new()
        {
            RoleId = "agent",
            TenantId = new TenantId(TestTenantId),
            Name = "Agent",
            SourceTemplateId = "agent",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        public Task<IReadOnlyList<TenantRole>> ListAsync(TenantId tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TenantRole>>([s_agent]);

        public Task<TenantRole?> GetByIdAsync(TenantId tenantId, string roleId, CancellationToken ct) =>
            Task.FromResult<TenantRole?>(roleId == s_agent.RoleId ? s_agent : null);

        public Task SaveAsync(TenantRole role, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteAsync(TenantId tenantId, string roleId, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetPermissionsAsync(TenantId tenantId, string roleId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task SetPermissionsAsync(TenantId tenantId, string roleId, IReadOnlyList<string> permissionIds, CancellationToken ct) =>
            Task.CompletedTask;

        public Task CloneFromTemplateAsync(TenantId tenantId, string roleId, string templateId, string name, string? description, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<int> GetUserCountAsync(TenantId tenantId, string roleId, CancellationToken ct) => Task.FromResult(0);
    }
}
