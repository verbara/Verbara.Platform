using Verbara.Platform.Api.Endpoints;
using Verbara.Sdk.Pro.Licensing;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

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
            LastValidatedAt: DateTimeOffset.UtcNow,
            InGrace: false,
            GracePeriodRemaining: null,
            Blocked: false);

        dto.IsValid.Should().BeTrue();
        dto.LicenseId.Should().Be("lic-001");
        dto.Licensee.Should().Be("Acme Corp");
        dto.Status.Should().Be("Valid");
        dto.LicensedFeatures.Should().HaveCount(3);
        dto.MaxNodes.Should().Be(5);
        dto.InGrace.Should().BeFalse();
        dto.GracePeriodRemaining.Should().BeNull();
        dto.Blocked.Should().BeFalse();
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
            LastValidatedAt: default,
            InGrace: false,
            GracePeriodRemaining: null,
            Blocked: true);

        dto.IsValid.Should().BeFalse();
        dto.LicenseId.Should().BeNull();
        dto.Licensee.Should().BeNull();
        dto.LicensedFeatures.Should().BeEmpty();
        dto.Blocked.Should().BeTrue();
    }

    // ─── ComputeGraceState — R5.2 PC.4 / triage limitation #11 ──────────────────
    //
    // These tests cover the pure mapping function so the grace-state surface is
    // verified without a full WebApplicationFactory. The endpoint integration
    // tests in ManagementSystemEndpointTests assert wire-level behaviour.

    [Fact]
    public void ComputeGraceState_ShouldReturnInGraceWithRemaining_WhenLicenseExpiredButWithinGraceWindow()
    {
        // ExpiresAt = 2 days ago, GracePeriod = 7 days → remaining = 5 days.
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var expiresAt = now.AddDays(-2);
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(LicenseValidationResult.GracePeriod);
        status.ExpiresAt.Returns(expiresAt);

        var (inGrace, remaining, blocked) = ManagementSystemEndpoints.ComputeGraceState(
            status, gracePeriod: TimeSpan.FromDays(7), now);

        inGrace.Should().BeTrue();
        remaining.Should().Be(TimeSpan.FromDays(5));
        blocked.Should().BeFalse();
    }

    [Fact]
    public void ComputeGraceState_ShouldReturnBlocked_WhenLicenseExpiredAndGraceExhausted()
    {
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(LicenseValidationResult.Expired);
        status.ExpiresAt.Returns(DateTimeOffset.UtcNow.AddDays(-30));

        var (inGrace, remaining, blocked) = ManagementSystemEndpoints.ComputeGraceState(
            status, gracePeriod: TimeSpan.FromDays(7), DateTimeOffset.UtcNow);

        inGrace.Should().BeFalse();
        remaining.Should().BeNull();
        blocked.Should().BeTrue();
    }

    [Fact]
    public void ComputeGraceState_ShouldReturnBlocked_WhenLicenseInvalid()
    {
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(LicenseValidationResult.Invalid);

        var (inGrace, remaining, blocked) = ManagementSystemEndpoints.ComputeGraceState(
            status, gracePeriod: TimeSpan.FromDays(7), DateTimeOffset.UtcNow);

        inGrace.Should().BeFalse();
        remaining.Should().BeNull();
        blocked.Should().BeTrue();
    }

    [Fact]
    public void ComputeGraceState_ShouldReturnDefaults_WhenLicenseValid()
    {
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(LicenseValidationResult.Valid);
        status.ExpiresAt.Returns(DateTimeOffset.UtcNow.AddDays(180));

        var (inGrace, remaining, blocked) = ManagementSystemEndpoints.ComputeGraceState(
            status, gracePeriod: TimeSpan.FromDays(7), DateTimeOffset.UtcNow);

        inGrace.Should().BeFalse();
        remaining.Should().BeNull();
        blocked.Should().BeFalse();
    }

    [Fact]
    public void ComputeGraceState_ShouldClampToZero_WhenInGracePastNominalRemaining()
    {
        // Edge case: LastResult is GracePeriod but the wall-clock has already
        // crept past ExpiresAt + GracePeriod (e.g. tracker hasn't refreshed yet).
        // Should clamp to TimeSpan.Zero rather than emit a negative value.
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(LicenseValidationResult.GracePeriod);
        status.ExpiresAt.Returns(now.AddDays(-30));

        var (inGrace, remaining, _) = ManagementSystemEndpoints.ComputeGraceState(
            status, gracePeriod: TimeSpan.FromDays(7), now);

        inGrace.Should().BeTrue();
        remaining.Should().Be(TimeSpan.Zero);
    }

    // ─── BuildLicenseInfo — only a signed licence speaks for its fields ─────────

    [Fact]
    public void BuildLicenseInfo_ShouldListNoFeaturesOrIdentity_WhenTheLoadedLicenceFailedValidation()
    {
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(LicenseValidationResult.Invalid);
        status.LicensedFeatures.Returns(LicenseFeature.Dialer | LicenseFeature.Cluster);
        status.LicenseId.Returns("lic-someone-else");
        status.Licensee.Returns("Someone Else Inc");
        status.ExpiresAt.Returns(DateTimeOffset.UtcNow.AddDays(365));
        status.MaxNodes.Returns(9);

        var dto = ManagementSystemEndpoints.BuildLicenseInfo(status, TimeSpan.FromDays(7), DateTimeOffset.UtcNow);

        dto.LicensedFeatures.Should().BeEmpty("an unsigned licence grants and names nothing");
        dto.LicenseId.Should().BeNull();
        dto.Licensee.Should().BeNull();
        dto.ExpiresAt.Should().BeNull();
        dto.MaxNodes.Should().Be(0);
        dto.Status.Should().Be("Invalid");
        dto.Blocked.Should().BeTrue();
    }

    [Theory]
    [InlineData(LicenseValidationResult.Valid)]
    [InlineData(LicenseValidationResult.GracePeriod)]
    [InlineData(LicenseValidationResult.MissingFeature)]
    [InlineData(LicenseValidationResult.Expired)]
    [InlineData(LicenseValidationResult.UnauthorizedImage)]
    public void BuildLicenseInfo_ShouldListTheLicenceFields_WhenTheLicenceIsSigned(LicenseValidationResult result)
    {
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(result);
        status.LicensedFeatures.Returns(LicenseFeature.Dialer);
        status.LicenseId.Returns("lic-001");
        status.Licensee.Returns("Acme Corp");
        status.MaxNodes.Returns(3);

        var dto = ManagementSystemEndpoints.BuildLicenseInfo(status, TimeSpan.FromDays(7), DateTimeOffset.UtcNow);

        dto.LicensedFeatures.Should().Equal(["Dialer"]);
        dto.LicenseId.Should().Be("lic-001");
        dto.Licensee.Should().Be("Acme Corp");
        dto.MaxNodes.Should().Be(3);
    }

    [Fact]
    public void ComputeGraceState_ShouldReturnBlocked_WhenTheImageIsNotAuthorized()
    {
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(LicenseValidationResult.UnauthorizedImage);

        var (inGrace, remaining, blocked) = ManagementSystemEndpoints.ComputeGraceState(
            status, gracePeriod: TimeSpan.FromDays(7), DateTimeOffset.UtcNow);

        inGrace.Should().BeFalse();
        remaining.Should().BeNull();
        blocked.Should().BeTrue();
    }
}
