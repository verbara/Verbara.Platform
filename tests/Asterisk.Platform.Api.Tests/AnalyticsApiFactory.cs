using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.Analytics;
using Asterisk.Sdk.Pro.CallAnalytics.Domain;
using Asterisk.Sdk.Pro.CallAnalytics.Store;
using Asterisk.Sdk.Pro.Dialer.Campaign;
using Asterisk.Sdk.Pro.Dialer.Contacts;
using Asterisk.Sdk.Pro.Dialer.Dispositions;
using Asterisk.Sdk.Pro.Dialer.Models;
using Asterisk.Sdk.Pro.EventStore;
using Asterisk.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Security.Cryptography;
using System.Text;

namespace Asterisk.Platform.Api.Tests;

/// <summary>
/// WebApplicationFactory with in-memory analytics stores and auth support.
/// Registers InMemoryCompletedSessionStore, InMemoryCallAnalyticsStore, InMemoryIntervalSnapshotStore
/// so analytics endpoints can be exercised without Postgres.
/// </summary>
public sealed class AnalyticsApiFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "analytics-test-key-88888";
    public const string TestTenantId = "tenant-analytics-001";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

    public InMemoryCompletedSessionStore CdrStore { get; } = new();
    public InMemoryCallAnalyticsStore QaStore { get; } = new();
    public InMemoryIntervalSnapshotStore SnapshotStore { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Replace IApiKeyStore with test key
            var akDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IApiKeyStore));
            if (akDescriptor is not null) services.Remove(akDescriptor);

            var apiKeyStore = Substitute.For<IApiKeyStore>();
            var apiKey = new ApiKey
            {
                KeyId = EntityId.From("analytics-test-key-id"),
                TenantId = new TenantId(TestTenantId),
                Name = "Analytics Test Key",
                HashedKey = s_hashedKey,
                Scopes = ["*"],
                IsRevoked = false,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            apiKeyStore.GetByHashAsync(s_hashedKey, Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<ApiKey?>(apiKey));
            services.AddSingleton(apiKeyStore);

            // Disable license enforcement in tests
            services.Configure<LicenseOptions>(o => o.EnforcementMode = EnforcementMode.Disabled);
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            // Register in-memory analytics stores (replace existing Postgres registrations if present)
            UpsertStore<ICompletedSessionStore>(services, CdrStore);
            UpsertStore<ICallAnalyticsStore>(services, QaStore);
            UpsertStore<IIntervalSnapshotStore>(services, SnapshotStore);

            // Register in-memory campaign/disposition stores needed for endpoint resolution
            if (!services.Any(d => d.ServiceType == typeof(CampaignStoreBase)))
            {
                var campaignStore = new InMemoryCampaignStore();
                services.AddSingleton<InMemoryCampaignStore>(campaignStore);
                services.AddSingleton<CampaignStoreBase>(campaignStore);
                services.AddSingleton<CampaignLifecycleManager>(sp =>
                    new CampaignLifecycleManager(
                        sp.GetRequiredService<CampaignStoreBase>(),
                        sp.GetRequiredService<ILogger<CampaignLifecycleManager>>()));
            }
            if (!services.Any(d => d.ServiceType == typeof(ContactListStoreBase)))
            {
                var contactStore = new InMemoryContactListStore();
                services.AddSingleton<InMemoryContactListStore>(contactStore);
                services.AddSingleton<ContactListStoreBase>(contactStore);
            }
            if (!services.Any(d => d.ServiceType == typeof(DispositionCodeStoreBase)))
            {
                var dispositionStore = new InMemoryDispositionCodeStore();
                services.AddSingleton<InMemoryDispositionCodeStore>(dispositionStore);
                services.AddSingleton<DispositionCodeStoreBase>(dispositionStore);
            }
        });

        return base.CreateHost(builder);
    }

    private static void UpsertStore<TService>(IServiceCollection services, TService instance)
        where TService : class
    {
        // Remove any existing registration (e.g. Postgres), then add in-memory
        var descriptors = services.Where(d => d.ServiceType == typeof(TService)).ToList();
        foreach (var d in descriptors) services.Remove(d);
        services.AddSingleton<TService>(instance);
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TestTenantId);
        return client;
    }

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}

// ─── In-Memory Store Implementations ─────────────────────────────────────────

public sealed class InMemoryCompletedSessionStore : ICompletedSessionStore
{
    private readonly ConcurrentDictionary<string, CompletedSessionRow> _rows = new();

    public ValueTask UpsertAsync(CompletedSessionRow row, CancellationToken ct = default)
    {
        _rows[$"{row.TenantId}:{row.SessionId}"] = row;
        return ValueTask.CompletedTask;
    }

    public ValueTask<CompletedSessionRow?> GetAsync(string tenantId, string sessionId, CancellationToken ct = default)
    {
        _rows.TryGetValue($"{tenantId}:{sessionId}", out var row);
        return new ValueTask<CompletedSessionRow?>(row);
    }

    public ValueTask<IReadOnlyList<CompletedSessionRow>> QueryAsync(
        string tenantId, CompletedSessionQuery query, CancellationToken ct = default)
    {
        var rows = _rows.Values
            .Where(r => r.TenantId == tenantId)
            .AsEnumerable();

        if (query.From is not null)
            rows = rows.Where(r => r.StartedAt >= query.From.Value);
        if (query.To is not null)
            rows = rows.Where(r => r.StartedAt <= query.To.Value);
        if (query.QueueName is not null)
            rows = rows.Where(r => r.QueueName == query.QueueName);
        if (query.AgentId is not null)
            rows = rows.Where(r => r.AgentId == query.AgentId);
        if (query.Direction is not null)
            rows = rows.Where(r => r.Direction == query.Direction.Value);

        IReadOnlyList<CompletedSessionRow> result = rows
            .OrderByDescending(r => r.StartedAt)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToList();

        return new ValueTask<IReadOnlyList<CompletedSessionRow>>(result);
    }

    public ValueTask<CompletedSessionStats> GetStatsAsync(
        string tenantId, DateTimeOffset from, DateTimeOffset until, string? serverId = null, CancellationToken ct = default)
    {
        var rows = _rows.Values
            .Where(r => r.TenantId == tenantId && r.StartedAt >= from && r.StartedAt <= until)
            .ToList();
        if (serverId is not null)
            rows = rows.Where(r => r.ServerId == serverId).ToList();

        var total = rows.Count;
        var answered = rows.Count(r => r.ConnectedAt is not null);
        var failed = rows.Count(r => r.FinalState == 9);
        var stats = new CompletedSessionStats(
            total, answered, failed,
            total > 0 ? rows.Average(r => r.DurationMs) : 0,
            answered > 0 ? rows.Where(r => r.TalkTimeMs.HasValue).Average(r => r.TalkTimeMs!.Value) : 0,
            answered > 0 ? rows.Where(r => r.WaitTimeMs.HasValue).Average(r => r.WaitTimeMs!.Value) : 0,
            total > 0 ? rows.Average(r => r.HoldTimeMs) : 0);
        return new ValueTask<CompletedSessionStats>(stats);
    }
}

public sealed class InMemoryCallAnalyticsStore : ICallAnalyticsStore
{
    private readonly ConcurrentDictionary<string, CallAnalysisResult> _results = new();

    public ValueTask SaveAsync(CallAnalysisResult result, CancellationToken ct = default)
    {
        _results[$"{result.TenantId}:{result.SessionId}"] = result;
        return ValueTask.CompletedTask;
    }

    public ValueTask<CallAnalysisResult?> GetAsync(string sessionId, string tenantId, CancellationToken ct = default)
    {
        _results.TryGetValue($"{tenantId}:{sessionId}", out var result);
        return new ValueTask<CallAnalysisResult?>(result);
    }

    public ValueTask<IReadOnlyList<CallAnalysisResult>> QueryAsync(CallAnalyticsQuery query, CancellationToken ct = default)
    {
        var results = _results.Values
            .Where(r => r.TenantId == query.TenantId)
            .AsEnumerable();

        if (query.From is not null)
            results = results.Where(r => r.AnalyzedAt >= query.From.Value);
        if (query.To is not null)
            results = results.Where(r => r.AnalyzedAt <= query.To.Value);
        if (query.MinQaScore is not null)
            results = results.Where(r => r.QualityScore is not null &&
                r.QualityScore.MaxPossibleScore > 0 &&
                r.QualityScore.TotalScore / r.QualityScore.MaxPossibleScore >= query.MinQaScore.Value);
        if (query.HasComplianceViolations is not null)
            results = results.Where(r => (r.ComplianceViolations.Count > 0) == query.HasComplianceViolations.Value);

        IReadOnlyList<CallAnalysisResult> result = results
            .OrderByDescending(r => r.AnalyzedAt)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToList();

        return new ValueTask<IReadOnlyList<CallAnalysisResult>>(result);
    }
}

public sealed class InMemoryIntervalSnapshotStore : IIntervalSnapshotStore
{
    private readonly List<IntervalSnapshot> _snapshots = [];

    public ValueTask UpsertAsync(IntervalSnapshot snapshot, CancellationToken ct = default)
    {
        lock (_snapshots)
        {
            var existing = _snapshots.FindIndex(s =>
                s.TenantId == snapshot.TenantId &&
                s.QueueName == snapshot.QueueName &&
                s.ServerId == snapshot.ServerId &&
                s.IntervalStart == snapshot.IntervalStart);
            if (existing >= 0)
                _snapshots[existing] = snapshot;
            else
                _snapshots.Add(snapshot);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask UpsertAgentAsync(AgentSnapshot snapshot, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public ValueTask UpsertCampaignAsync(CampaignSnapshot snapshot, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<IntervalSnapshot>> QueryAsync(
        string tenantId, DateTimeOffset from, DateTimeOffset until,
        string? queueName = null, string? serverId = null,
        CancellationToken ct = default)
    {
        lock (_snapshots)
        {
            IReadOnlyList<IntervalSnapshot> result = _snapshots
                .Where(s => s.TenantId == tenantId &&
                            s.IntervalStart >= from &&
                            s.IntervalStart <= until)
                .Where(s => queueName is null || s.QueueName == queueName)
                .Where(s => serverId is null || s.ServerId == serverId)
                .OrderBy(s => s.IntervalStart)
                .ToList();
            return new ValueTask<IReadOnlyList<IntervalSnapshot>>(result);
        }
    }

    public ValueTask<IReadOnlyList<AgentSnapshot>> QueryAgentAsync(
        string tenantId, DateTimeOffset from, DateTimeOffset until,
        string? agentId = null, CancellationToken ct = default)
    {
        return new ValueTask<IReadOnlyList<AgentSnapshot>>(Array.Empty<AgentSnapshot>());
    }
}
