using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.Services.QaAvailability;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests.QaAvailability;

/// <summary>
/// qa-scores-are-produced-or-not-shown (design D1, task 2.1): <c>GET /api/v1/analytics/qa-availability</c> through the
/// real host. The test host configures no analytics or dialer database, so post-call analysis is not registered there;
/// the scenarios of a host that registered it swap only that startup fact (<see cref="QaAvailabilityService"/> built
/// with <c>analysisRegistered: true</c> over the host's own setup and licence guard), and read the body the host writes.
/// </summary>
public sealed class QaAvailabilityEndpointTests
{
    private const string Route = "/api/v1/analytics/qa-availability";
    private const string QaListRoute = "/api/v1/analytics/qa";

    private static WebApplicationFactory<Program> RegisteredAnalysis(
        UnifiedPlatformApiFactory factory, bool callAnalyticsLicensed) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var guard = Substitute.For<ILicenseGuard>();
            var allowed = new LicenseGuardResult(true, null);
            var denied = new LicenseGuardResult(false, LicenseBlockReason.NotLicensed);
            guard.CanExecute(Arg.Any<LicenseFeature>()).Returns(allowed);
            guard.CanExecuteAsync(Arg.Any<LicenseFeature>(), Arg.Any<CancellationToken>()).Returns(allowed);
            guard.CanExecuteAsync(LicenseFeature.CallAnalytics, Arg.Any<CancellationToken>())
                .Returns(callAnalyticsLicensed ? allowed : denied);
            services.AddSingleton(guard);

            foreach (var d in services.Where(d => d.ServiceType == typeof(QaAvailabilityService)).ToList())
                services.Remove(d);
            services.AddSingleton(sp => new QaAvailabilityService(
                analysisRegistered: true,
                sp.GetRequiredService<IQaAnalysisSetup>(),
                sp.GetRequiredService<ILicenseGuard>()));
        }));

    private static HttpClient Authenticated(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {UnifiedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", UnifiedPlatformApiFactory.TestTenantId);
        return client;
    }

    private static async Task<JsonObject> GetBodyAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Route);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Verbara.Platform.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public async Task GetQaAvailability_ShouldEqualTheNoTranscriptSourceFixture_WhenAnalysisIsRegisteredWithoutASource()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        await using var registered = RegisteredAnalysis(factory, callAnalyticsLicensed: true);
        using var client = Authenticated(registered);
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Join(RepoRoot(), "tests", "fixtures", "qa-availability", "qa-availability.v1.json")))!;

        using var response = await client.GetAsync(Route);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Be(fixture.ToJsonString(), "the body carries every member, explicit nulls included, in the fixture's order");
    }

    [Fact]
    public async Task GetQaAvailability_ShouldAnswerAnalysisNotRegistered_WhenNoAnalyticsOrDialerDatabaseIsConfigured()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var body = await GetBodyAsync(client);

        body["available"]!.GetValue<bool>().Should().BeFalse();
        body["analysisRunning"]!.GetValue<bool>().Should().BeFalse();
        body["reason"]!.GetValue<string>().Should().Be("analysis_not_registered");
    }

    [Fact]
    public async Task GetQaAvailability_ShouldAnswerNotLicensed_WhenTheLicenceLacksCallAnalytics()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        await using var registered = RegisteredAnalysis(factory, callAnalyticsLicensed: false);
        using var client = Authenticated(registered);

        var body = await GetBodyAsync(client);

        body["available"]!.GetValue<bool>().Should().BeFalse();
        body["analysisRunning"]!.GetValue<bool>().Should().BeFalse();
        body["reason"]!.GetValue<string>().Should().Be("not_licensed");
        body.ContainsKey("transcriptSource").Should().BeTrue("the member is always written");
        body["transcriptSource"].Should().BeNull("step 1 configures no transcript source");
    }

    [Fact]
    public async Task GetQaAvailability_ShouldBeRefusedLikeTheQaList_WhenTheCallerIsAnAgent()
    {
        await using var factory = new NonAdminAuthenticatedApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var availability = await client.GetAsync(Route);
        using var qaList = await client.GetAsync(QaListRoute);

        availability.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        availability.StatusCode.Should().Be(qaList.StatusCode);
    }

    [Fact]
    public async Task GetQaAvailability_ShouldBeRefusedLikeTheQaList_WhenTheLicenceLacksAnalytics()
    {
        await using var factory = new WithoutAnalyticsLicenceFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var availability = await client.GetAsync(Route);
        using var qaList = await client.GetAsync(QaListRoute);

        availability.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        availability.StatusCode.Should().Be(qaList.StatusCode);
    }

    [Fact]
    public async Task GetQaAvailability_ShouldBeRefusedLikeTheQaList_WhenTheCallerIsNotAuthenticated()
    {
        await using var factory = new UnifiedPlatformApiFactory();
        using var client = factory.CreateClient();

        using var availability = await client.GetAsync(Route);
        using var qaList = await client.GetAsync(QaListRoute);

        availability.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        availability.StatusCode.Should().Be(qaList.StatusCode);
    }

    // The base factory registers its licence status last, so the narrowed licence goes through its hook rather than
    // WithWebHostBuilder.
    private sealed class WithoutAnalyticsLicenceFactory : UnifiedPlatformApiFactory
    {
        protected override LicenseFeature LicensedFeatures => LicenseFeature.All & ~LicenseFeature.Analytics;
    }
}
