namespace Verbara.Platform.Api.Services;

using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

public sealed partial class CampaignMetricsPoller : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the per-tick <see cref="ResiliencePolicy"/> that wraps each
    /// metrics-poll pass. Circuit-open skips the current tick; the next 30s tick retries.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.campaign-metrics-poller";

    private readonly CampaignStoreBase _campaignStore;
    private readonly PlatformEventBus _eventBus;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<CampaignMetricsPoller> _logger;
    private readonly IClusterLeader _webhooksLeader;
    private readonly Dictionary<long, CampaignMetricsSnapshot> _previousSnapshots = new();

    // Whether the last poll that reached the publish loop ran as the campaign-metrics webhook lease
    // holder. A faulted or circuit-open tick never reaches the loop, so it leaves this untouched.
    private bool _publishedAsLeader;

    public CampaignMetricsPoller(
        CampaignStoreBase campaignStore,
        PlatformEventBus eventBus,
        ILogger<CampaignMetricsPoller> logger,
        [FromKeyedServices(CampaignMetricsLeaderResources.Webhooks)] IClusterLeader webhooksLeader,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _campaignStore = campaignStore;
        _eventBus = eventBus;
        _logger = logger;
        _webhooksLeader = webhooksLeader;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _policy.ExecuteAsync(
                        ResiliencePolicyKey,
                        async innerCt =>
                        {
                            await PollMetricsAsync(innerCt);
                            return 0;
                        },
                        stoppingToken);
                }
                catch (CircuitBreakerOpenException)
                {
                    LogCircuitOpen();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogPollError(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(nameof(CampaignMetricsPoller), fatalEx.Message, fatalEx);
            throw;
        }
    }

    internal async Task PollMetricsAsync(CancellationToken ct)
    {
        var metrics = await _campaignStore.GetAllActiveCampaignMetricsAsync(ct);

        // Read the lease after the store call, immediately before publishing: the round-trip can span
        // a demotion and a re-acquire, and only this tick's view decides whether its events reach the
        // dispatcher (design D5 point 4). On gaining the lease, publish every active campaign as if seen
        // for the first time: as a follower this replica recorded changes whose webhooks were dropped.
        var isLeader = _webhooksLeader.IsLeader;
        if (isLeader && !_publishedAsLeader)
            _previousSnapshots.Clear();
        _publishedAsLeader = isLeader;

        foreach (var item in metrics)
        {
            var snapshot = item.Metrics;

            // An event without a tenant reaches no tenant's subscribers; never publish one.
            if (string.IsNullOrWhiteSpace(item.TenantId))
            {
                LogMissingTenant(snapshot.CampaignId, snapshot.CampaignName);
                continue;
            }

            if (_previousSnapshots.TryGetValue(snapshot.CampaignId, out var previous))
            {
                // Only publish if metrics changed
                if (previous.ContactsDialed != snapshot.ContactsDialed ||
                    previous.ContactsRemaining != snapshot.ContactsRemaining ||
                    previous.ConnectRate != snapshot.ConnectRate ||
                    previous.AbandonRate != snapshot.AbandonRate)
                {
                    _eventBus.Publish(new CampaignMetricsUpdatedEvent(
                        item.TenantId,
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
                    item.TenantId, snapshot.CampaignId,
                    snapshot.ContactsDialed, snapshot.ContactsRemaining,
                    snapshot.ConnectRate, snapshot.AbandonRate, 0));
            }

            _previousSnapshots[snapshot.CampaignId] = snapshot;
        }

        // Remove campaigns no longer active
        var activeIds = metrics.Select(m => m.Metrics.CampaignId).ToHashSet();
        foreach (var id in _previousSnapshots.Keys.Where(k => !activeIds.Contains(k)).ToList())
            _previousSnapshots.Remove(id);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skipping metrics for campaign {CampaignId} ({CampaignName}): the store returned no tenant id")]
    private partial void LogMissingTenant(long campaignId, string campaignName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error polling campaign metrics")]
    private partial void LogPollError(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.campaign-metrics-poller — skipping tick")]
    private partial void LogCircuitOpen();

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}
