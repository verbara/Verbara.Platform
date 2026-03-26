namespace Asterisk.Platform.Api.Services;

using Asterisk.Sdk.Pro.Dialer.Campaign;
using Asterisk.Sdk.Pro.Dialer.Models;
using Asterisk.Platform.Core;

public sealed class CampaignMetricsPoller : BackgroundService
{
    private readonly CampaignStoreBase _campaignStore;
    private readonly PlatformEventBus _eventBus;
    private readonly ILogger<CampaignMetricsPoller> _logger;
    private readonly Dictionary<long, CampaignMetricsSnapshot> _previousSnapshots = new();

    public CampaignMetricsPoller(
        CampaignStoreBase campaignStore,
        PlatformEventBus eventBus,
        ILogger<CampaignMetricsPoller> logger)
    {
        _campaignStore = campaignStore;
        _eventBus = eventBus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PollMetricsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
#pragma warning disable CA1848 // Use LoggerMessage delegates
                _logger.LogError(ex, "Error polling campaign metrics");
#pragma warning restore CA1848
            }
        }
    }

    private async Task PollMetricsAsync(CancellationToken ct)
    {
        // Use empty string for tenant — multi-tenant polling gets all tenants
        var metrics = await _campaignStore.GetActiveCampaignMetricsAsync("", ct);

        foreach (var snapshot in metrics)
        {
            if (_previousSnapshots.TryGetValue(snapshot.CampaignId, out var previous))
            {
                // Only publish if metrics changed
                if (previous.ContactsDialed != snapshot.ContactsDialed ||
                    previous.ContactsRemaining != snapshot.ContactsRemaining ||
                    previous.ConnectRate != snapshot.ConnectRate ||
                    previous.AbandonRate != snapshot.AbandonRate)
                {
                    _eventBus.Publish(new CampaignMetricsUpdatedEvent(
                        "", // TenantId — will need per-tenant polling in future
                        snapshot.CampaignId,
                        snapshot.ContactsDialed,
                        snapshot.ContactsRemaining,
                        snapshot.ConnectRate,
                        snapshot.AbandonRate,
                        0)); // ActiveCalls = 0 in v0.3.0
                }
            }
            else
            {
                // First time seeing this campaign — publish initial metrics
                _eventBus.Publish(new CampaignMetricsUpdatedEvent(
                    "", snapshot.CampaignId,
                    snapshot.ContactsDialed, snapshot.ContactsRemaining,
                    snapshot.ConnectRate, snapshot.AbandonRate, 0));
            }

            _previousSnapshots[snapshot.CampaignId] = snapshot;
        }

        // Remove campaigns no longer active
        var activeIds = metrics.Select(m => m.CampaignId).ToHashSet();
        foreach (var id in _previousSnapshots.Keys.Where(k => !activeIds.Contains(k)).ToList())
            _previousSnapshots.Remove(id);
    }
}
