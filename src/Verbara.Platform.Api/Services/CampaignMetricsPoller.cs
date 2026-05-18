namespace Verbara.Platform.Api.Services;

using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Platform.Core;
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
    private readonly Dictionary<long, CampaignMetricsSnapshot> _previousSnapshots = new();

    public CampaignMetricsPoller(
        CampaignStoreBase campaignStore,
        PlatformEventBus eventBus,
        ILogger<CampaignMetricsPoller> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _campaignStore = campaignStore;
        _eventBus = eventBus;
        _logger = logger;
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Error polling campaign metrics")]
    private partial void LogPollError(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.campaign-metrics-poller — skipping tick")]
    private partial void LogCircuitOpen();

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}
