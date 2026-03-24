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
/// Single WebApplicationFactory that registers all in-memory stores (campaign + analytics)
/// and provides authenticated HTTP client support. Replaces CampaignApiFactory and
/// AnalyticsApiFactory so both test classes share one consistent factory implementation.
/// </summary>
public sealed class UnifiedPlatformApiFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "unified-test-key-77777";
    public const string TestTenantId = "tenant-unified-001";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

    // Analytics store instances exposed for direct seeding in tests
    public InMemoryCompletedSessionStore CdrStore { get; } = new();
    public InMemoryCallAnalyticsStore QaStore { get; } = new();
    public InMemoryIntervalSnapshotStore SnapshotStore { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // ── Auth ──────────────────────────────────────────────────────────
            var akDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IApiKeyStore));
            if (akDescriptor is not null) services.Remove(akDescriptor);

            var apiKeyStore = Substitute.For<IApiKeyStore>();
            var apiKey = new ApiKey
            {
                KeyId = EntityId.From("unified-test-key-id"),
                TenantId = new TenantId(TestTenantId),
                Name = "Unified Test Key",
                HashedKey = s_hashedKey,
                Scopes = ["*"],
                IsRevoked = false,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            apiKeyStore.GetByHashAsync(s_hashedKey, Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<ApiKey?>(apiKey));
            services.AddSingleton(apiKeyStore);

            // ── Licensing ─────────────────────────────────────────────────────
            services.Configure<LicenseOptions>(o => o.EnforcementMode = EnforcementMode.Disabled);
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            // ── Campaign stores ───────────────────────────────────────────────
            if (!services.Any(d => d.ServiceType == typeof(CampaignStoreBase)))
            {
                services.AddSingleton<InMemoryCampaignStore>();
                services.AddSingleton<CampaignStoreBase>(sp => sp.GetRequiredService<InMemoryCampaignStore>());
                services.AddSingleton<CampaignLifecycleManager>(sp =>
                    new CampaignLifecycleManager(
                        sp.GetRequiredService<CampaignStoreBase>(),
                        sp.GetRequiredService<ILogger<CampaignLifecycleManager>>()));
            }

            if (!services.Any(d => d.ServiceType == typeof(ContactListStoreBase)))
            {
                services.AddSingleton<InMemoryContactListStore>();
                services.AddSingleton<ContactListStoreBase>(sp => sp.GetRequiredService<InMemoryContactListStore>());
            }

            if (!services.Any(d => d.ServiceType == typeof(DispositionCodeStoreBase)))
            {
                services.AddSingleton<InMemoryDispositionCodeStore>();
                services.AddSingleton<DispositionCodeStoreBase>(sp => sp.GetRequiredService<InMemoryDispositionCodeStore>());
            }

            // ── Analytics stores ──────────────────────────────────────────────
            UpsertStore<ICompletedSessionStore>(services, CdrStore);
            UpsertStore<ICallAnalyticsStore>(services, QaStore);
            UpsertStore<IIntervalSnapshotStore>(services, SnapshotStore);
        });

        return base.CreateHost(builder);
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TestTenantId);
        return client;
    }

    private static void UpsertStore<TService>(IServiceCollection services, TService instance)
        where TService : class
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(TService)).ToList();
        foreach (var d in descriptors) services.Remove(d);
        services.AddSingleton<TService>(instance);
    }

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}

// ─── Campaign In-Memory Store Implementations ─────────────────────────────────

internal sealed class InMemoryCampaignStore : CampaignStoreBase
{
    private long _nextId = 1;
    private readonly ConcurrentDictionary<long, Campaign> _campaigns = new();
    private readonly List<(string TenantId, long CampaignId, long ContactId, DateTimeOffset ScheduledAt, string? AgentId)> _callbacks = [];

    public override ValueTask<IReadOnlyList<Campaign>> GetActiveCampaignsAsync(string tenantId, CancellationToken ct)
    {
        var result = _campaigns.Values
            .Where(c => c.TenantId == tenantId && c.Status is CampaignStatus.Active or CampaignStatus.Paused)
            .ToList();
        return new ValueTask<IReadOnlyList<Campaign>>(result);
    }

    public override ValueTask<Campaign?> GetCampaignAsync(string tenantId, long campaignId, CancellationToken ct)
    {
        _campaigns.TryGetValue(campaignId, out var campaign);
        if (campaign?.TenantId != tenantId) campaign = null;
        return new ValueTask<Campaign?>(campaign);
    }

    public override ValueTask UpdateCampaignStatusAsync(string tenantId, long campaignId, CampaignStatus status, CancellationToken ct)
    {
        if (_campaigns.TryGetValue(campaignId, out var campaign) && campaign.TenantId == tenantId)
            campaign.Status = status;
        return ValueTask.CompletedTask;
    }

    public override ValueTask<DialerSettings> GetDialerSettingsAsync(string tenantId, CancellationToken ct)
        => new ValueTask<DialerSettings>(new DialerSettings());

    public override ValueTask SaveCallbackAsync(string tenantId, long campaignId, long contactId, DateTimeOffset scheduledAt, string? agentId, CancellationToken ct)
    {
        _callbacks.Add((tenantId, campaignId, contactId, scheduledAt, agentId));
        return ValueTask.CompletedTask;
    }

    public override ValueTask<IReadOnlyList<(long CampaignId, long ContactId, DateTimeOffset ScheduledAt, string? AgentId)>>
        GetPendingCallbacksAsync(string tenantId, DateTimeOffset until, CancellationToken ct)
    {
        IReadOnlyList<(long, long, DateTimeOffset, string?)> result = _callbacks
            .Where(c => c.TenantId == tenantId && c.ScheduledAt <= until)
            .Select(c => (c.CampaignId, c.ContactId, c.ScheduledAt, c.AgentId))
            .ToList();
        return new ValueTask<IReadOnlyList<(long, long, DateTimeOffset, string?)>>(result);
    }

    public override ValueTask<long> CreateCampaignAsync(string tenantId, Campaign campaign, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        campaign.Id = id;
        _campaigns[id] = campaign;
        return new ValueTask<long>(id);
    }

    public override ValueTask UpdateCampaignAsync(string tenantId, Campaign campaign, CancellationToken ct)
    {
        _campaigns[campaign.Id] = campaign;
        return ValueTask.CompletedTask;
    }

    public override ValueTask DeleteCampaignAsync(string tenantId, long campaignId, CancellationToken ct)
    {
        _campaigns.TryRemove(campaignId, out _);
        return ValueTask.CompletedTask;
    }

    public override ValueTask<IReadOnlyList<Campaign>> ListCampaignsAsync(string tenantId, int page, int pageSize, CancellationToken ct)
    {
        var result = _campaigns.Values
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return new ValueTask<IReadOnlyList<Campaign>>(result);
    }

    public override ValueTask<int> CountCampaignsAsync(string tenantId, CancellationToken ct)
    {
        var count = _campaigns.Values.Count(c => c.TenantId == tenantId);
        return new ValueTask<int>(count);
    }

    public override ValueTask UpdateCallAttemptDispositionAsync(string tenantId, long callAttemptId, long dispositionId, string? agentComment, CancellationToken ct)
        => ValueTask.CompletedTask;

    public override ValueTask<CampaignMetricsSnapshot> GetCampaignMetricsAsync(string tenantId, long campaignId, CancellationToken ct)
    {
        _campaigns.TryGetValue(campaignId, out var campaign);
        var snapshot = new CampaignMetricsSnapshot(campaignId, campaign?.Name ?? "", campaign?.Status ?? CampaignStatus.Draft, 0, 0, 0.0, 0.0);
        return new ValueTask<CampaignMetricsSnapshot>(snapshot);
    }

    public override ValueTask<IReadOnlyList<CampaignMetricsSnapshot>> GetActiveCampaignMetricsAsync(string tenantId, CancellationToken ct)
    {
        IReadOnlyList<CampaignMetricsSnapshot> result = _campaigns.Values
            .Where(c => c.TenantId == tenantId && c.Status is CampaignStatus.Active or CampaignStatus.Paused)
            .Select(c => new CampaignMetricsSnapshot(c.Id, c.Name, c.Status, 0, 0, 0.0, 0.0))
            .ToList();
        return new ValueTask<IReadOnlyList<CampaignMetricsSnapshot>>(result);
    }
}

internal sealed class InMemoryContactListStore : ContactListStoreBase
{
    private long _nextListId = 1;
    private long _nextContactId = 1;
    private readonly ConcurrentDictionary<long, ContactList> _lists = new();
    private readonly ConcurrentDictionary<long, List<Contact>> _contacts = new();

    public override ValueTask<ContactList> CreateContactListAsync(string tenantId, long campaignId, ContactList list, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextListId);
        list.Id = id;
        _lists[id] = list;
        _contacts[id] = [];
        return new ValueTask<ContactList>(list);
    }

    public override ValueTask<IReadOnlyList<ContactList>> ListContactListsAsync(string tenantId, long campaignId, CancellationToken ct)
    {
        var result = _lists.Values
            .Where(l => l.TenantId == tenantId && l.CampaignId == campaignId)
            .ToList();
        return new ValueTask<IReadOnlyList<ContactList>>(result);
    }

    public override ValueTask DeleteContactListAsync(string tenantId, long listId, CancellationToken ct)
    {
        _lists.TryRemove(listId, out _);
        _contacts.TryRemove(listId, out _);
        return ValueTask.CompletedTask;
    }

    public override ValueTask<int> ImportContactsAsync(string tenantId, long listId, IReadOnlyList<ContactImportRow> rows, CancellationToken ct)
    {
        var list = _contacts.GetOrAdd(listId, _ => []);
        foreach (var row in rows)
        {
            var id = Interlocked.Increment(ref _nextContactId);
            list.Add(new Contact
            {
                Id = id,
                ContactListId = listId,
                FirstName = row.FirstName,
                LastName = row.LastName,
            });
        }
        if (_lists.TryGetValue(listId, out var cl))
            cl.TotalContacts += rows.Count;
        return new ValueTask<int>(rows.Count);
    }

    public override ValueTask<(IReadOnlyList<Contact> Items, int TotalCount)> ListContactsAsync(string tenantId, long listId, int page, int pageSize, CancellationToken ct)
    {
        if (!_contacts.TryGetValue(listId, out var contacts))
            return new ValueTask<(IReadOnlyList<Contact>, int)>(([], 0));

        var total = contacts.Count;
        IReadOnlyList<Contact> items = contacts
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return new ValueTask<(IReadOnlyList<Contact>, int)>((items, total));
    }
}

internal sealed class InMemoryDispositionCodeStore : DispositionCodeStoreBase
{
    private long _nextId = 1;
    private readonly ConcurrentDictionary<long, DispositionCode> _codes = new();

    public override ValueTask<DispositionCode> CreateAsync(string tenantId, DispositionCode code, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        code.Id = id;
        _codes[id] = code;
        return new ValueTask<DispositionCode>(code);
    }

    public override ValueTask<IReadOnlyList<DispositionCode>> ListByCampaignAsync(string tenantId, long campaignId, CancellationToken ct)
    {
        IReadOnlyList<DispositionCode> result = _codes.Values
            .Where(d => d.TenantId == tenantId && d.CampaignId == campaignId)
            .OrderBy(d => d.SortOrder)
            .ToList();
        return new ValueTask<IReadOnlyList<DispositionCode>>(result);
    }

    public override ValueTask UpdateAsync(string tenantId, DispositionCode code, CancellationToken ct)
    {
        _codes[code.Id] = code;
        return ValueTask.CompletedTask;
    }

    public override ValueTask DeleteAsync(string tenantId, long dispositionCodeId, CancellationToken ct)
    {
        _codes.TryRemove(dispositionCodeId, out _);
        return ValueTask.CompletedTask;
    }
}

// ─── Analytics In-Memory Store Implementations ────────────────────────────────

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
