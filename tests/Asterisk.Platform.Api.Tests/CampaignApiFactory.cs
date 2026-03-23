using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.Dialer.Campaign;
using Asterisk.Sdk.Pro.Dialer.Contacts;
using Asterisk.Sdk.Pro.Dialer.Dispositions;
using Asterisk.Sdk.Pro.Dialer.Models;
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
/// WebApplicationFactory with in-memory campaign stores and auth support.
/// Registers InMemoryCampaignStore, InMemoryContactListStore, InMemoryDispositionCodeStore
/// so campaign endpoints can be exercised without Postgres.
/// </summary>
public sealed class CampaignApiFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "campaign-test-key-99999";
    public const string TestTenantId = "tenant-campaign-001";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

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
                KeyId = EntityId.From("campaign-test-key-id"),
                TenantId = new TenantId(TestTenantId),
                Name = "Campaign Test Key",
                HashedKey = s_hashedKey,
                Scopes = ["*"],
                IsRevoked = false,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            apiKeyStore.GetByHashAsync(s_hashedKey, Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<ApiKey?>(apiKey));
            services.AddSingleton(apiKeyStore);

            // Disable license enforcement in tests and provide dummy public key byte[]
            services.Configure<LicenseOptions>(o => o.EnforcementMode = EnforcementMode.Disabled);
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            // Register in-memory campaign stores (only if not already registered)
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

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}

// ─── In-Memory Store Implementations ─────────────────────────────────────────

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

    public int CallbackCount => _callbacks.Count;
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
