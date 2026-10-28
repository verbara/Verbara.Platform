using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests.LicensedAgentMetering;

/// <summary>
/// The ledger's licence id comes only from a licence whose signature verified: the tracker keeps the
/// fields of a licence that failed validation, and those fields name nobody.
/// </summary>
public sealed class ProLicenseIdSourceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(LicenseValidationResult.Valid)]
    [InlineData(LicenseValidationResult.GracePeriod)]
    [InlineData(LicenseValidationResult.MissingFeature)]
    [InlineData(LicenseValidationResult.Expired)]
    [InlineData(LicenseValidationResult.UnauthorizedImage)]
    public void CurrentLicenseId_ShouldBeTheLicenceId_WhenTheLicenceIsSigned(LicenseValidationResult result)
    {
        var tracker = new LicenseStatusTracker(_time);
        tracker.Update(result, Key("lic-0001"));

        new ProLicenseIdSource(tracker).CurrentLicenseId.Should().Be("lic-0001");
    }

    [Fact]
    public void CurrentLicenseId_ShouldBeNull_WhenTheLoadedLicenceFailedValidation()
    {
        var tracker = new LicenseStatusTracker(_time);
        tracker.Update(LicenseValidationResult.Invalid, Key("lic-someone-else"));

        var source = new ProLicenseIdSource(tracker);

        source.CurrentLicenseId.Should().BeNull("an unsigned licence names nobody the ledger may anchor on");
        source.LoadCompleted.Should().BeTrue("the load attempt did complete");
    }

    [Fact]
    public void CurrentLicenseId_ShouldBeNull_WhenTheResultIsNotAKnownValue()
    {
        var tracker = new LicenseStatusTracker(_time);
        tracker.Update((LicenseValidationResult)999, Key("lic-0001"));

        new ProLicenseIdSource(tracker).CurrentLicenseId.Should().BeNull();
    }

    private static LicenseKey Key(string id) => new(
        id, "Licensee", LicenseTier.SelfHostStartup, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        LicenseFeature.None, 25, 1, "sig");
}
