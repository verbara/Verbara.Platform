using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Billing;

/// <summary>
/// Transitions <see cref="InvoiceStatus.Draft"/> invoices that carry an <see cref="UsageType.AiAnalysis"/>
/// overage line item to <see cref="InvoiceStatus.Issued"/> once their grace window
/// (<c>PeriodEnd + DunningConfig.OverageGraceDays</c>) has elapsed, stamping
/// <see cref="Invoice.IssuedAt"/> and <see cref="Invoice.DueDate"/>. Mirrors
/// <see cref="DunningService"/>'s hosted-service template (keyed resilience policy, a scope per cycle,
/// and an <c>internal</c> per-cycle method so tests drive it without the timer loop).
/// </summary>
public sealed partial class OverageInvoiceIssuanceWorker : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the per-cycle <see cref="ResiliencePolicy"/> that wraps each
    /// issuance pass. Circuit-open skips the current cycle; the next tick retries.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.overage-issuance";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OverageInvoiceIssuanceWorker> _logger;
    private readonly DunningConfig _config;
    private readonly ResiliencePolicy _policy;

    public OverageInvoiceIssuanceWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<OverageInvoiceIssuanceWorker> logger,
        IOptions<DunningConfig> config,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config.Value;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _policy.ExecuteAsync(
                        ResiliencePolicyKey,
                        async innerCt =>
                        {
                            await ProcessIssuanceCycleAsync(innerCt);
                            return 0;
                        },
                        stoppingToken);
                }
                catch (CircuitBreakerOpenException)
                {
                    // Circuit open — skip this cycle, next tick retries.
                    LogCircuitOpen(_logger);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogIssuanceCycleFailed(_logger, ex);
                }

                await Task.Delay(TimeSpan.FromHours(_config.CheckIntervalHours), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(_logger, nameof(OverageInvoiceIssuanceWorker), fatalEx.Message, fatalEx);
            throw;
        }
    }

    internal async Task ProcessIssuanceCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var invoiceStore = sp.GetRequiredService<IInvoiceStore>();
        var clock = sp.GetRequiredService<IClock>();

        var now = clock.UtcNow;

        var drafts = await invoiceStore.ListByStatusAsync(InvoiceStatus.Draft, ct);
        foreach (var invoice in drafts)
        {
            var hasOverage = false;
            foreach (var item in invoice.LineItems)
            {
                if (item.UsageType == UsageType.AiAnalysis && item.OverageQuantity > 0m)
                {
                    hasOverage = true;
                    break;
                }
            }

            if (!hasOverage)
                continue;

            if (invoice.PeriodEnd.AddDays(_config.OverageGraceDays) > now)
                continue;

            try
            {
                invoice.Status = InvoiceStatus.Issued;
                invoice.IssuedAt = now;
                invoice.DueDate = now.AddDays(_config.PaymentTermDays);
                await invoiceStore.SaveAsync(invoice, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogIssuanceFailed(_logger, invoice.InvoiceId.Value, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Overage invoice issuance cycle failed")]
    private static partial void LogIssuanceCycleFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to issue overage invoice {InvoiceId}")]
    private static partial void LogIssuanceFailed(ILogger logger, string invoiceId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.overage-issuance — skipping cycle")]
    private static partial void LogCircuitOpen(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private static partial void LogWorkerCrash(ILogger logger, string workerName, string reason, Exception ex);
}
