using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Platform.Api.DependencyInjection;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Pins the startup-migrator registrar contract: which migrators are registered, in
/// what order, and that the whole set is skipped when the deployment has no Postgres.
/// Registration-only assertions on a bare <see cref="ServiceCollection"/> — no host,
/// no database — mirroring <c>RealtimeSyncingStoresRegistrarTests</c>.
/// </summary>
public sealed class StartupMigratorsRegistrarTests
{
    private const string FakeConnectionString =
        "Host=localhost;Database=test;Username=postgres;Password=postgres";

    private static readonly string[] ExpectedMigratorsInStartOrder =
    [
        "OidcClientSecretEncryptionMigrator",
        "UserMfaEncryptionMigrator",
        "TenantLlmConfigSeedMigrator",
    ];

    [Fact]
    public void AddPlatformStartupMigrators_ShouldRegisterTheThreeMigratorsInStartOrder_WhenConnectionStringPresent()
    {
        var services = new ServiceCollection();

        services.AddPlatformStartupMigrators(FakeConnectionString);

        HostedServiceNames(services).Should().Equal(
            ExpectedMigratorsInStartOrder,
            because: "IHostedService instances start in registration order, and each migrator "
                   + "must run after the one before it — the order is part of the contract");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AddPlatformStartupMigrators_ShouldRegisterNothing_WhenConnectionStringMissing(string? connectionString)
    {
        var services = new ServiceCollection();

        services.AddPlatformStartupMigrators(connectionString);

        HostedServiceNames(services).Should().BeEmpty(
            because: "every migrator needs the NpgsqlDataSource that only AddPostgresStorage "
                   + "registers; on the InMemory path there is nothing to migrate and "
                   + "registering them would crash the host at start");
    }

    [Fact]
    public void AddPlatformStartupMigrators_ShouldReturnServiceCollection_ForFluentChaining()
    {
        var services = new ServiceCollection();

        var result = services.AddPlatformStartupMigrators(FakeConnectionString);

        result.Should().BeSameAs(services);
    }

    private static List<string> HostedServiceNames(IServiceCollection services) =>
        services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType?.Name ?? d.ImplementationInstance?.GetType().Name ?? "<factory>")
            .ToList();
}
