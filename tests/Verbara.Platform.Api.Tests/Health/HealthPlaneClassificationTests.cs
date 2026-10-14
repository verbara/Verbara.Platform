using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Verbara.Platform.Api.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Health;

/// <summary>
/// readiness-without-asterisk, design D1 (spec <c>api-readiness-scope</c>, requirements 1, 2 and 4): every health
/// check the Api registers lands in exactly one plane through the real endpoint predicates of
/// <see cref="PlatformHealthExtensions"/>: readiness (<c>/health/ready</c>, the HTTP plane), voice
/// (<c>/health/voice</c>) or neither.
///
/// <para>The table below is the explicit classification. The host test reads the registrations from the booted
/// host's <see cref="HealthCheckServiceOptions"/>, never from a hand-written list, so a new check, from Platform or
/// from a Pro or Sdk pin bump, fails here until it is classified. A name the Testing host does not register stays in
/// the table: the test fails on the unknown, not on the absent. The table test runs the same predicates over every
/// row with the tags its producer registers it with, so the rows the Testing host does not register (the
/// session-fed Pro engines among them) are exercised too.</para>
/// </summary>
public sealed class HealthPlaneClassificationTests
{
    private enum Plane
    {
        Readiness,
        Voice,
        Neither,
    }

    private sealed record Row(Plane Plane, string[] ProducerTags);

    private static readonly string[] Ready = ["ready"];
    private static readonly string[] VoiceOnly = ["voice"];
    private static readonly string[] Untagged = [];

    /// <summary>
    /// Every check name the Api can register, with the tags its producer gives it. The Pro rows were read from the
    /// consumed pin <c>2.18.0-pro</c>; <c>stt</c> and <c>tts</c> register only when AgentAssist providers are
    /// configured.
    /// </summary>
    private static readonly Dictionary<string, Row> Classification = new(StringComparer.Ordinal)
    {
        // Platform (PlatformHealthExtensions.AddPlatformHealthChecks). `postgres` registers only when an
        // NpgsqlDataSource is registered, so the Testing host has none.
        ["services"] = new(Plane.Readiness, Ready),
        ["retention"] = new(Plane.Readiness, Ready),
        ["postgres"] = new(Plane.Readiness, Ready),
        // `asterisk` is registered with the tag `voice` only (design D2): this pod's primary AMI connection and the
        // voice heartbeats, reported on /health/voice and never on /health/ready.
        ["asterisk"] = new(Plane.Voice, VoiceOnly),

        // Pro engine checks, all tagged `ready` by their producer (Pro/ADR-0017). `dialer-engine` stays in
        // readiness because of the community-boot contract.
        ["dialer-engine"] = new(Plane.Readiness, Ready),
        ["csatrunner-orchestrator"] = new(Plane.Readiness, Ready),
        ["analytics-live-queue-writer"] = new(Plane.Readiness, Ready),

        // The session-fed Pro engines: tagged `ready` by Pro, fed by the AMI call-session stream, so voice by name
        // (Pro spec `engine-health-semantics`; owner decision 2026-10-13).
        ["eventstore-subscriber"] = new(Plane.Voice, Ready),
        ["analytics-aggregator"] = new(Plane.Voice, Ready),
        ["callanalytics-engine"] = new(Plane.Voice, Ready),
        ["agentassist-engine"] = new(Plane.Voice, Ready),

        // Untagged Sdk checks: AddVerbaraMultiServer, AddVerbaraSessionsMultiServer and AddVerbaraPush.
        ["verbara-pool"] = new(Plane.Neither, Untagged),
        ["sessions"] = new(Plane.Neither, Untagged),
        ["push"] = new(Plane.Neither, Untagged),

        // Untagged Pro AgentAssist provider checks.
        ["stt"] = new(Plane.Neither, Untagged),
        ["tts"] = new(Plane.Neither, Untagged),
    };

    private sealed class NoOpHealthCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Healthy());
    }

    private static HealthCheckRegistration Registration(string name, IEnumerable<string> tags) =>
        new(name, new NoOpHealthCheck(), failureStatus: null, tags: tags);

    /// <summary>The plane the real predicates put a registration in, or <c>null</c> when they admit it to both.</summary>
    private static Plane? PlaneOf(HealthCheckRegistration registration)
    {
        var readiness = PlatformHealthExtensions.IsReadinessCheck(registration);
        var voice = PlatformHealthExtensions.IsVoicePlaneCheck(registration);
        if (readiness && voice)
            return null;
        return readiness ? Plane.Readiness : voice ? Plane.Voice : Plane.Neither;
    }

    [Fact]
    public async Task HealthCheckRegistrations_ShouldEachLandInExactlyTheirClassifiedPlane_WhenHostBoots()
    {
        await using var factory = new ReadinessScopeTests.ReadinessFactory();
        var registrations = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        registrations.Should().NotBeEmpty("the booted host registers its health checks");

        var unknown = registrations
            .Where(r => !Classification.ContainsKey(r.Name))
            .Select(r => $"{r.Name} [{string.Join(",", r.Tags)}]")
            .ToList();
        unknown.Should().BeEmpty(
            "every registered health check must be classified as readiness, voice or neither before it ships "
            + "(design D1); add the name to the table with its plane");

        var misplaced = registrations
            .Select(r => (r.Name, Expected: Classification[r.Name].Plane, Actual: PlaneOf(r)))
            .Where(x => x.Actual != x.Expected)
            .Select(x => $"{x.Name}: expected {x.Expected}, predicates give {x.Actual?.ToString() ?? "both"}")
            .ToList();
        misplaced.Should().BeEmpty("each registered check lands in exactly its classified plane");
    }

    [Fact]
    public void ClassificationTable_ShouldMatchThePredicates_WhenEachRowCarriesItsProducerTags()
    {
        var misplaced = Classification
            .Select(kv => (kv.Key, Expected: kv.Value.Plane, Actual: PlaneOf(Registration(kv.Key, kv.Value.ProducerTags))))
            .Where(x => x.Actual != x.Expected)
            .Select(x => $"{x.Key}: expected {x.Expected}, predicates give {x.Actual?.ToString() ?? "both"}")
            .ToList();

        misplaced.Should().BeEmpty("every classified name lands in exactly its plane through the real predicates");
    }

    [Fact]
    public void IsReadinessCheck_ShouldAdmitAnUnclassifiedReadyCheck_WhenItIsNotVoicePlane()
    {
        // The voice set is a denylist (design D1): a new check tagged `ready` gates readiness until it is
        // classified, the safe way to fail.
        var registration = Registration("a-future-pro-engine", Ready);

        PlaneOf(registration).Should().Be(Plane.Readiness);
    }

    [Fact]
    public void IsVoicePlaneCheck_ShouldClaimACheckTaggedBothReadyAndVoice_WhenItsProducerTagsItVoice()
    {
        // The producer-side tag (tasks.md 4.2) works whether or not the producer keeps `ready` beside `voice`:
        // a `voice` tag always wins, so the two predicates never both admit a check.
        var registration = Registration("a-producer-tagged-engine", ["ready", "voice"]);

        PlaneOf(registration).Should().Be(Plane.Voice);
    }

    [Fact]
    public void IsVoicePlaneCheck_ShouldClaimASessionFedEngine_WhenItsNameDiffersOnlyInCase()
    {
        // The health-check service treats names case-insensitively, so the voice set does too.
        var registration = Registration("EventStore-Subscriber", Ready);

        PlaneOf(registration).Should().Be(Plane.Voice);
    }
}
