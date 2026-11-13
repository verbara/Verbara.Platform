using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services.QaAvailability;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests.QaAvailability;

/// <summary>
/// qa-scores-are-produced-or-not-shown (design D1): the availability read. <c>reason</c> names the first missing piece in
/// the order analysis registered, licensed, transcript source, scorecard; <c>analysisRunning</c> needs the first three,
/// <c>available</c> all four. The configuration inputs come from the host seam, faked here per tenant; the licence is
/// asked of the guard on every read.
/// </summary>
public sealed class QaAvailabilityServiceTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private static ILicenseGuard Guard(bool allowed)
    {
        var guard = Substitute.For<ILicenseGuard>();
        var result = new LicenseGuardResult(allowed, allowed ? null : LicenseBlockReason.NotLicensed);
        guard.CanExecuteAsync(Arg.Any<LicenseFeature>(), Arg.Any<CancellationToken>()).Returns(result);
        guard.CanExecute(Arg.Any<LicenseFeature>()).Returns(result);
        return guard;
    }

    private sealed class FakeSetup(string? transcriptSource, string? speakerMode, params (string Tenant, QaTenantAnalysisSetup Setup)[] tenants)
        : IQaAnalysisSetup
    {
        public string? TranscriptSource { get; } = transcriptSource;

        public string? SpeakerMode { get; } = speakerMode;

        public QaTenantAnalysisSetup ForTenant(string tenantId)
        {
            foreach (var (tenant, setup) in tenants)
            {
                if (tenant == tenantId)
                    return setup;
            }

            return QaTenantAnalysisSetup.None;
        }
    }

    private static readonly QaTenantAnalysisSetup Full = new(HasScorecard: true, TopicsConfigured: true, ComplianceConfigured: false);

    private static FakeSetup Configured() =>
        new(QaAvailabilityDto.TranscriptSourceRecording, QaAvailabilityDto.SpeakerModeTwoTrack, (TenantA, Full));

    [Fact]
    public async Task GetAsync_ShouldAnswerAnalysisNotRegistered_WhenTheAnalysisIsNotRegistered()
    {
        var guard = Guard(allowed: false);
        var service = new QaAvailabilityService(analysisRegistered: false, Configured(), guard);

        var body = await service.GetAsync(TenantA, CancellationToken.None);

        body.Available.Should().BeFalse();
        body.AnalysisRunning.Should().BeFalse();
        body.Reason.Should().Be(QaAvailabilityDto.ReasonAnalysisNotRegistered);
        body.TranscriptSource.Should().Be(QaAvailabilityDto.TranscriptSourceRecording);
        body.SpeakerMode.Should().BeNull();
        body.TopicsConfigured.Should().BeNull();
        body.ComplianceConfigured.Should().BeNull();
        body.Backlog.Should().BeNull();
        await guard.DidNotReceive().CanExecuteAsync(Arg.Any<LicenseFeature>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_ShouldAnswerNotLicensed_WhenTheGuardRefusesCallAnalytics()
    {
        var guard = Guard(allowed: false);
        var service = new QaAvailabilityService(analysisRegistered: true, Configured(), guard);

        var body = await service.GetAsync(TenantA, CancellationToken.None);

        body.Available.Should().BeFalse();
        body.AnalysisRunning.Should().BeFalse();
        body.Reason.Should().Be(QaAvailabilityDto.ReasonNotLicensed);
        body.TranscriptSource.Should().Be(QaAvailabilityDto.TranscriptSourceRecording);
        body.SpeakerMode.Should().BeNull();
        body.TopicsConfigured.Should().BeNull();
        body.ComplianceConfigured.Should().BeNull();
        body.Backlog.Should().BeNull();
        await guard.Received(1).CanExecuteAsync(LicenseFeature.CallAnalytics, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_ShouldAnswerNoTranscriptSource_WhenLicensedWithoutASource()
    {
        var service = new QaAvailabilityService(analysisRegistered: true, new FakeSetup(null, null, (TenantA, Full)), Guard(allowed: true));

        var body = await service.GetAsync(TenantA, CancellationToken.None);

        body.Available.Should().BeFalse();
        body.AnalysisRunning.Should().BeFalse();
        body.Reason.Should().Be(QaAvailabilityDto.ReasonNoTranscriptSource);
        body.TranscriptSource.Should().BeNull();
        body.SpeakerMode.Should().BeNull();
        body.TopicsConfigured.Should().BeNull();
        body.ComplianceConfigured.Should().BeNull();
        body.Backlog.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldAnswerNoScorecardForOneTenantOnly_WhenOnlyTheOtherTenantHasAScorecard()
    {
        var setup = new FakeSetup(
            QaAvailabilityDto.TranscriptSourceRecording,
            QaAvailabilityDto.SpeakerModeMono,
            (TenantA, Full),
            (TenantB, new QaTenantAnalysisSetup(HasScorecard: false, TopicsConfigured: false, ComplianceConfigured: true)));
        var service = new QaAvailabilityService(analysisRegistered: true, setup, Guard(allowed: true));

        var a = await service.GetAsync(TenantA, CancellationToken.None);
        var b = await service.GetAsync(TenantB, CancellationToken.None);

        a.Available.Should().BeTrue();
        a.AnalysisRunning.Should().BeTrue();
        a.Reason.Should().BeNull();
        a.TopicsConfigured.Should().BeTrue();
        a.ComplianceConfigured.Should().BeFalse();

        b.Available.Should().BeFalse();
        b.AnalysisRunning.Should().BeTrue();
        b.Reason.Should().Be(QaAvailabilityDto.ReasonNoScorecard);
        b.TranscriptSource.Should().Be(QaAvailabilityDto.TranscriptSourceRecording);
        b.SpeakerMode.Should().Be(QaAvailabilityDto.SpeakerModeMono);
        b.TopicsConfigured.Should().BeFalse();
        b.ComplianceConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_ShouldReportAnalysisRunning_WhenRegisteredLicensedAndSourcedEvenWithoutAScorecard()
    {
        var setup = new FakeSetup(QaAvailabilityDto.TranscriptSourceRecording, QaAvailabilityDto.SpeakerModeTwoTrack);
        var service = new QaAvailabilityService(analysisRegistered: true, setup, Guard(allowed: true));

        var body = await service.GetAsync(TenantA, CancellationToken.None);

        body.AnalysisRunning.Should().BeTrue();
        body.Available.Should().BeFalse();
        body.Reason.Should().Be(QaAvailabilityDto.ReasonNoScorecard);
        body.SpeakerMode.Should().Be(QaAvailabilityDto.SpeakerModeTwoTrack);
        body.TopicsConfigured.Should().BeFalse();
        body.ComplianceConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task GetAsync_ShouldAskTheGuardOnEveryRead_WhenTheLicenceChangesWhileRunning()
    {
        var guard = Substitute.For<ILicenseGuard>();
        guard.CanExecuteAsync(LicenseFeature.CallAnalytics, Arg.Any<CancellationToken>())
            .Returns(new LicenseGuardResult(true, null), new LicenseGuardResult(false, LicenseBlockReason.NotLicensed));
        var service = new QaAvailabilityService(analysisRegistered: true, Configured(), guard);

        var first = await service.GetAsync(TenantA, CancellationToken.None);
        var second = await service.GetAsync(TenantA, CancellationToken.None);

        first.Available.Should().BeTrue();
        second.Available.Should().BeFalse();
        second.Reason.Should().Be(QaAvailabilityDto.ReasonNotLicensed);
        await guard.Received(2).CanExecuteAsync(LicenseFeature.CallAnalytics, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_ShouldAnswerNoTranscriptSource_WhenTheStepOneSetupIsUsed()
    {
        var service = new QaAvailabilityService(analysisRegistered: true, UnconfiguredQaAnalysisSetup.Instance, Guard(allowed: true));

        var body = await service.GetAsync(TenantA, CancellationToken.None);

        body.Should().Be(new QaAvailabilityDto(false, false, QaAvailabilityDto.ReasonNoTranscriptSource, null, null, null, null, null));
        UnconfiguredQaAnalysisSetup.Instance.ForTenant(TenantA).Should().Be(QaTenantAnalysisSetup.None);
    }
}

public sealed class QaAvailabilityExtensionsTests
{
    [Theory]
    [InlineData(false, QaAvailabilityDto.ReasonAnalysisNotRegistered)]
    [InlineData(true, QaAvailabilityDto.ReasonNoTranscriptSource)]
    public async Task AddQaAvailability_ShouldResolveTheStepOneSetup_WhenNoSetupIsRegistered(bool analysisRegistered, string reason)
    {
        var guard = Substitute.For<ILicenseGuard>();
        guard.CanExecuteAsync(Arg.Any<LicenseFeature>(), Arg.Any<CancellationToken>()).Returns(new LicenseGuardResult(true, null));
        var services = new ServiceCollection();
        services.AddSingleton(guard);

        services.AddQaAvailability(analysisRegistered);
        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<QaAvailabilityService>();

        var body = await service.GetAsync("tenant-a", CancellationToken.None);

        body.Available.Should().BeFalse();
        body.AnalysisRunning.Should().BeFalse();
        body.Reason.Should().Be(reason);
        body.TranscriptSource.Should().BeNull();
    }
}
