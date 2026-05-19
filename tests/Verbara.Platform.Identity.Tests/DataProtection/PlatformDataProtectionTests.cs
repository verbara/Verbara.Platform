using Verbara.Platform.Identity.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Identity.Tests.DataProtection;

/// <summary>
/// Unit-level verification of <see cref="PlatformDataProtectionOptions"/> +
/// <see cref="PlatformDataProtectionExtensions.AddPlatformDataProtection"/>.
/// The Postgres-backed round-trip path lives in the integration suite (real
/// NpgsqlDataSource) since Dapper has no in-memory provider equivalent to
/// EF Core's InMemoryDatabase. Ephemeral + option-validation paths exercise
/// the public surface here without a DB.
/// </summary>
public sealed class PlatformDataProtectionTests
{
    [Fact]
    public void AddPlatformDataProtection_ShouldRegisterDataProtectionProvider_WhenEphemeralSelected()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPlatformDataProtection(opt =>
        {
            opt.ApplicationName = "Verbara.Platform.Test";
            opt.UseEphemeralKeysForTesting();
        });

        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IDataProtectionProvider>();

        provider.Should().NotBeNull("ephemeral path still wires the ASP.NET Core DataProtection stack");
    }

    [Fact]
    public void AddPlatformDataProtection_ShouldThrow_WhenNoPersistenceStrategyChosen()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddPlatformDataProtection(opt => opt.ApplicationName = "Verbara.Platform");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*UsePostgres*UseFileSystem*UseEphemeralKeysForTesting*");
    }

    [Fact]
    public void UseFileSystem_ShouldThrow_WhenPathBlank()
    {
        var options = new PlatformDataProtectionOptions();

        var act = () => options.UseFileSystem("   ");

        act.Should().Throw<ArgumentException>().WithMessage("*non-empty*");
    }

    [Fact]
    public void UseEphemeralKeysForTesting_ShouldFlagEphemeralMode()
    {
        var options = new PlatformDataProtectionOptions();

        options.UseEphemeralKeysForTesting();

        options.UseEphemeralForTesting.Should().BeTrue();
    }

    [Fact]
    public void UsePostgres_ShouldRejectNullDataSource()
    {
        var options = new PlatformDataProtectionOptions();

        var act = () => options.UsePostgres(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
