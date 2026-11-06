using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Tests.EventStore;

/// <summary>
/// call-records-are-written-once (design D1; task B.1): one Api replica of a clustered voice deployment — the real
/// <c>Program</c> with the event store registered (a <c>ConnectionStrings:Analytics</c>), the cluster's Postgres
/// transport and its per-resource leases (a <c>ConnectionStrings:Cluster</c>) and the voice leader lease
/// (<c>Asterisk:Ami:Hostname</c>). Everything else is the <see cref="PlatformApiFactory"/> shape: the Verbara hosted
/// services stripped, AMI and the server stubbed, the in-memory stores and the licensing stubs.
/// </summary>
/// <remarks>
/// Three seams are the test's, each the boundary a real replica does not control: the Sdk's call session source (one
/// <see cref="ICallSessionManager"/> shared by every replica of a test, as one PBX raises each call to every replica),
/// the voice leader lease for <see cref="VoiceLeaderResources.AmiOwner"/> (a fake whose answer the test sets), and the
/// event store's storage (a recording <see cref="ISessionEventStore"/> per replica, so each replica's appends are
/// counted apart) and, because a test host carries no license key, the runtime license guard. The event store's subscriber and its options are the ones <c>Program.cs</c> registered; the test
/// starts that subscriber itself, because <see cref="AuthenticatedPlatformApiFactory.StubVerbaraHostedServices"/>
/// strips every factory-registered hosted service.
/// </remarks>
public sealed class EventStoreReplicaFactory : WebApplicationFactory<Program>
{
    /// <summary>The Api's AMI host. It names no reachable server: AMI is stubbed and the cluster manager stripped.</summary>
    public const string AmiHostname = "asterisk.eventstore-replica.invalid";

    private readonly string _analyticsConnectionString;
    private readonly string _clusterConnectionString;
    private readonly ICallSessionManager _sessionManager;

    /// <summary>Creates one replica over the shared database, call session source and voice leader fake.</summary>
    public EventStoreReplicaFactory(
        string analyticsConnectionString,
        string clusterConnectionString,
        ICallSessionManager sessionManager,
        FakeVoiceLeader voiceLeader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(analyticsConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterConnectionString);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(voiceLeader);
        _analyticsConnectionString = analyticsConnectionString;
        _clusterConnectionString = clusterConnectionString;
        _sessionManager = sessionManager;
        VoiceLeader = voiceLeader;
    }

    /// <summary>This replica's voice leader lease.</summary>
    public FakeVoiceLeader VoiceLeader { get; }

    /// <summary>What this replica's event store appended.</summary>
    public RecordingSessionEventStore Store { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Analytics", _analyticsConnectionString);
        builder.UseSetting("ConnectionStrings:Cluster", _clusterConnectionString);
        builder.UseSetting("Asterisk:Ami:Hostname", AmiHostname);
        builder.UseSetting("Asterisk:Ami:Port", "5038");
        builder.UseSetting("Asterisk:Ami:Username", "eventstore-replica");
        builder.UseSetting("Asterisk:Ami:Password", "eventstore-replica");
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);

            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

            Replace(services, typeof(ICallSessionManager), ServiceDescriptor.Singleton(_sessionManager));
            Replace(services, typeof(ISessionEventStore), ServiceDescriptor.Singleton<ISessionEventStore>(Store));
            Replace(services, typeof(IFencedCallEventStore), null);
            Replace(services, typeof(ILicenseGuard), ServiceDescriptor.Singleton<ILicenseGuard>(new AllowAllLicenseGuard()));

            var leases = services
                .Where(d => d.ServiceType == typeof(IClusterLeader)
                    && Equals(d.ServiceKey, VoiceLeaderResources.AmiOwner))
                .ToList();
            foreach (var lease in leases)
                services.Remove(lease);
            services.AddKeyedSingleton<IClusterLeader>(VoiceLeaderResources.AmiOwner, VoiceLeader);
        });

        return base.CreateHost(builder);
    }

    /// <summary>
    /// The runtime license guard reads the license key itself, which a test host has none of; the event store's
    /// writer is what this host pins, not its license gate, so every feature is allowed.
    /// </summary>
    private sealed class AllowAllLicenseGuard : ILicenseGuard
    {
        private static readonly LicenseGuardResult Allowed = new(true, null);

        public ValueTask<LicenseGuardResult> CanExecuteAsync(LicenseFeature feature, CancellationToken ct) =>
            ValueTask.FromResult(Allowed);

        public LicenseGuardResult CanExecute(LicenseFeature feature) => Allowed;
    }

    private static void Replace(IServiceCollection services, Type serviceType, ServiceDescriptor? replacement)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == serviceType && !d.IsKeyedService).ToList())
            services.Remove(descriptor);
        if (replacement is not null)
            services.Add(replacement);
    }
}

/// <summary>The voice leader lease of one replica; the test says whether this replica holds it.</summary>
public sealed class FakeVoiceLeader(string instanceId, bool isLeader) : IClusterLeader
{
    private volatile bool _isLeader = isLeader;

    /// <inheritdoc />
    public string Resource => VoiceLeaderResources.AmiOwner;

    /// <inheritdoc />
    public bool IsLeader
    {
        get => _isLeader;
        set => _isLeader = value;
    }

    /// <inheritdoc />
    public string? CurrentLeaderInstanceId => _isLeader ? instanceId : null;
}

/// <summary>An unfenced event store that records every row one replica appended.</summary>
public sealed class RecordingSessionEventStore : ISessionEventStore
{
    private readonly Lock _gate = new();
    private readonly List<SessionEventRow> _rows = [];

    /// <summary>A snapshot of the appended rows, in append order.</summary>
    public IReadOnlyList<SessionEventRow> Rows
    {
        get
        {
            lock (_gate)
                return [.. _rows];
        }
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(SessionEventRow row, CancellationToken ct = default)
    {
        lock (_gate)
            _rows.Add(row);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask AppendBatchAsync(IReadOnlyList<SessionEventRow> rows, CancellationToken ct = default)
    {
        lock (_gate)
            _rows.AddRange(rows);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SessionEventRow>> GetEventsAsync(
        string tenantId, string sessionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<SessionEventRow> rows = [.. _rows.Where(r => r.TenantId == tenantId && r.SessionId == sessionId)];
            return ValueTask.FromResult(rows);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SessionEventRow>> GetEventsByTimeRangeAsync(
        string tenantId, DateTimeOffset from, DateTimeOffset until, int limit = 1000, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<SessionEventRow> rows = [.. _rows
                .Where(r => r.TenantId == tenantId && r.Timestamp >= from && r.Timestamp < until)
                .Take(limit)];
            return ValueTask.FromResult(rows);
        }
    }
}
