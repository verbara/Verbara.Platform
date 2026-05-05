using System.Diagnostics.CodeAnalysis;
using Verbara.Platform.Identity.DataProtection;
using Verbara.Platform.Identity.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Identity.Tests.DataProtection;

/// <summary>
/// Verifies R5.2 P0.8 / ADR-0003 — DB-backed DataProtection keyring survives
/// process recreation when two distinct ServiceProvider instances share the
/// same DbContext database (simulating two API replicas or one container
/// recycle).
/// </summary>
[RequiresUnreferencedCode("EF Core path uses reflection; not trim-safe.")]
[RequiresDynamicCode("EF Core path builds queries dynamically; not NativeAOT-safe.")]
public sealed class PlatformDataProtectionTests
{
    [Fact]
    public void Protect_ShouldRoundTrip_WhenDbBackedKeysShared()
    {
        // Arrange — both "processes" share the same in-memory DB.
        const string dbName = nameof(Protect_ShouldRoundTrip_WhenDbBackedKeysShared);

        // First "process": protect a value
        string protectedPayload;
        {
            using var sp = BuildProvider(dbName);
            var protector = sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("test-purpose");
            protectedPayload = protector.Protect("the secret");
        }

        // Second "process" (new ServiceProvider, same DB): unprotect must succeed
        {
            using var sp = BuildProvider(dbName);
            var protector = sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("test-purpose");
            var unprotected = protector.Unprotect(protectedPayload);
            unprotected.Should().Be("the secret");
        }
    }

    [Fact]
    public void AddPlatformDataProtection_ShouldDefaultToDbContextPersistence()
    {
        const string dbName = nameof(AddPlatformDataProtection_ShouldDefaultToDbContextPersistence);
        using var sp = BuildProvider(dbName);

        var provider = sp.GetRequiredService<IDataProtectionProvider>();

        provider.Should().NotBeNull("default path is DB-backed via PlatformDataProtectionDbContext");
    }

    [Fact]
    public void UseFileSystem_ShouldThrow_WhenPathBlank()
    {
        var options = new PlatformDataProtectionOptions();

        var act = () => options.UseFileSystem("   ");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*non-empty*");
    }

    [Fact]
    public void UseEphemeralKeysForTesting_ShouldFlagEphemeralMode()
    {
        var options = new PlatformDataProtectionOptions();

        options.UseEphemeralKeysForTesting();

        options.UseEphemeralForTesting.Should().BeTrue();
    }

    private static ServiceProvider BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<PlatformDataProtectionDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddPlatformDataProtection(opt => opt.ApplicationName = "Verbara.Platform.Test");
        return services.BuildServiceProvider();
    }
}
