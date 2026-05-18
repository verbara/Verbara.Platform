using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Pro.Licensing.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Licensing;

/// <summary>
/// Regression tests for the v2.3.1 fix to <c>src/Verbara.Platform.Api/Program.cs:307-325</c>.
/// </summary>
/// <remarks>
/// <para>
/// Pre-fix the Platform host called <c>AddSingleton(licensePublicKey)</c> unconditionally,
/// passing <see cref="System.Array.Empty{T}"/> when no operator-supplied
/// <c>Licensing:PublicKeyPath</c> was configured. That early registration won the DI race
/// against <see cref="LicensingServiceCollectionExtensions.AddProLicensing"/>'s
/// <c>TryAddSingleton&lt;byte[]&gt;(LicenseTrustAnchor.OfficialPublicKey)</c>, leaving
/// <see cref="LicenseValidationHostedService"/> with an empty trust anchor that caused
/// <c>ECDsa.ImportSubjectPublicKeyInfo</c> to throw on every license validation.
/// </para>
/// <para>
/// The fix narrows the early registration to operator overrides only. These tests pin
/// the contract: with no override → official trust anchor wins; with override → custom
/// trust anchor wins.
/// </para>
/// </remarks>
public sealed class LicenseTrustAnchorWiringTests
{
    [Fact]
    public void Resolved_ShouldBeOfficialPublicKey_WhenPublicKeyPathUnset()
    {
        // Arrange — mirror Program.cs L307-325 wiring exactly with no Licensing:PublicKeyPath set.
        var configuration = new ConfigurationBuilder().Build();
        var licenseConfig = configuration.GetSection("Licensing");
        var publicKeyPath = licenseConfig["PublicKeyPath"];

        var services = new ServiceCollection();
        if (!string.IsNullOrEmpty(publicKeyPath) && File.Exists(publicKeyPath))
        {
            services.AddSingleton<byte[]>(File.ReadAllBytes(publicKeyPath));
        }
        services.AddProLicensing(o =>
        {
            o.LicenseFilePath = "/tmp/license.lic";
        });

        // Act
        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<byte[]>();

        // Assert — the official trust anchor must win when no override is configured.
        resolved.Should().NotBeEmpty(
            "AddProLicensing.TryAddSingleton<byte[]>(LicenseTrustAnchor.OfficialPublicKey) " +
            "must register the default trust anchor when the host did not pre-register a custom one.");
        resolved.Should().Equal(LicenseTrustAnchor.OfficialPublicKey,
            "the resolved byte[] singleton MUST equal LicenseTrustAnchor.OfficialPublicKey " +
            "byte-for-byte. Any divergence means a host-side AddSingleton<byte[]>(...) is " +
            "shadowing the default — exactly the regression the v2.3.1 fix prevents.");
    }

    [Fact]
    public void Resolved_ShouldBeCustomKey_WhenPublicKeyPathPointsToValidFile()
    {
        // Arrange — operator-supplied custom trust anchor file.
        var customKey = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var tempPath = Path.Combine(Path.GetTempPath(), $"trust-anchor-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(tempPath, customKey);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Licensing:PublicKeyPath"] = tempPath,
                })
                .Build();
            var licenseConfig = configuration.GetSection("Licensing");
            var publicKeyPath = licenseConfig["PublicKeyPath"];

            var services = new ServiceCollection();
            if (!string.IsNullOrEmpty(publicKeyPath) && File.Exists(publicKeyPath))
            {
                services.AddSingleton<byte[]>(File.ReadAllBytes(publicKeyPath));
            }
            services.AddProLicensing(o =>
            {
                o.LicenseFilePath = "/tmp/license.lic";
            });

            // Act
            using var provider = services.BuildServiceProvider();
            var resolved = provider.GetRequiredService<byte[]>();

            // Assert — the custom operator-supplied key MUST win (TryAddSingleton no-ops).
            resolved.Should().Equal(customKey,
                "when Licensing:PublicKeyPath is configured AND the file exists, the host " +
                "pre-registers the operator-supplied trust anchor; AddProLicensing's default " +
                "registration becomes a no-op via TryAddSingleton<byte[]>.");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
