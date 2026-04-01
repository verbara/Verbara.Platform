using Asterisk.Platform.Api.Endpoints;
using FluentAssertions;
using Xunit;

namespace Asterisk.Platform.Api.Tests.Endpoints;

public sealed class ManagementSystemEndpointsTests
{
    [Fact]
    public void LicenseInfoDto_ShouldMapAllFields_WhenLicenseIsValid()
    {
        var dto = new LicenseInfoDto(
            IsValid: true,
            LicenseId: "lic-001",
            Licensee: "Acme Corp",
            Status: "Valid",
            ExpiresAt: new DateTimeOffset(2027, 12, 31, 0, 0, 0, TimeSpan.Zero),
            LicensedFeatures: ["Cluster", "Dialer", "Analytics"],
            MaxNodes: 5,
            LastValidatedAt: DateTimeOffset.UtcNow);

        dto.IsValid.Should().BeTrue();
        dto.LicenseId.Should().Be("lic-001");
        dto.Licensee.Should().Be("Acme Corp");
        dto.Status.Should().Be("Valid");
        dto.LicensedFeatures.Should().HaveCount(3);
        dto.MaxNodes.Should().Be(5);
    }

    [Fact]
    public void LicenseInfoDto_ShouldBeInvalid_WhenNoLicenseLoaded()
    {
        var dto = new LicenseInfoDto(
            IsValid: false,
            LicenseId: null,
            Licensee: null,
            Status: "Invalid",
            ExpiresAt: null,
            LicensedFeatures: [],
            MaxNodes: 0,
            LastValidatedAt: default);

        dto.IsValid.Should().BeFalse();
        dto.LicenseId.Should().BeNull();
        dto.Licensee.Should().BeNull();
        dto.LicensedFeatures.Should().BeEmpty();
    }
}
