using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Billing;

public sealed partial class DunningService : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the per-cycle <see cref="ResiliencePolicy"/> that wraps each
    /// dunning pass. Circuit-open skips the current cycle; the next hourly tick retries.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.dunning";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DunningService> _logger;
    private readonly DunningConfig _config;
    private readonly ResiliencePolicy _policy;

    public DunningService(
        IServiceScopeFactory scopeFactory,
        ILogger<DunningService> logger,
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _policy.ExecuteAsync(
                    ResiliencePolicyKey,
                    async innerCt =>
                    {
                        await ProcessDunningCycleAsync(innerCt);
                        return 0;
                    },
                    stoppingToken);
            }
            catch (CircuitBreakerOpenException)
            {
                // Circuit open — skip this cycle, next hourly tick retries.
                LogCircuitOpen(_logger);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogDunningCycleFailed(_logger, ex);
            }

            await Task.Delay(TimeSpan.FromHours(_config.CheckIntervalHours), stoppingToken);
        }
    }

    internal async Task ProcessDunningCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var invoiceStore = sp.GetRequiredService<IInvoiceStore>();
        var dunningStore = sp.GetRequiredService<IDunningStore>();
        var tenantStore = sp.GetRequiredService<ITenantStore>();
        var lifecycleHandlers = sp.GetServices<ITenantLifecycleHandler>();
        var clock = sp.GetRequiredService<IClock>();

        var now = clock.UtcNow;

        // Phase 1: Detect new overdue invoices
        var issuedInvoices = await invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, ct);
        foreach (var invoice in issuedInvoices)
        {
            if (invoice.DueDate is null || invoice.DueDate >= now)
                continue;

            var existing = await dunningStore.GetByInvoiceAsync(invoice.InvoiceId.Value, ct);
            if (existing is not null)
                continue;

            try
            {
                var record = new DunningRecord
                {
                    DunningId = EntityId.New().Value,
                    TenantId = invoice.TenantId.Value,
                    InvoiceId = invoice.InvoiceId.Value,
                    CurrentStage = TenantStatus.Warning,
                    StartedAt = invoice.DueDate.Value,
                };

                invoice.PaymentStatus = PaymentStatus.Overdue;
                await invoiceStore.SaveAsync(invoice, ct);
                await dunningStore.UpsertAsync(record, ct);
                await tenantStore.UpdateStatusAsync(invoice.TenantId.Value, TenantStatus.Warning, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDunningCreateFailed(_logger, invoice.InvoiceId.Value, ex);
            }
        }

        // Phase 2: Escalate existing records
        var activeRecords = await dunningStore.ListActiveAsync(ct);
        foreach (var record in activeRecords)
        {
            if (record.IsPaused)
                continue;

            try
            {
                var days = (now - record.StartedAt).TotalDays;
                TenantStatus? newStage = null;
                PaymentStatus? newPayment = null;

                if (days >= _config.PendingDeletionDays && record.CurrentStage != TenantStatus.PendingDeletion)
                {
                    newStage = TenantStatus.PendingDeletion;
                    newPayment = PaymentStatus.WrittenOff;
                }
                else if (days >= _config.SuspendedDays && record.CurrentStage is TenantStatus.Warning or TenantStatus.Degraded)
                {
                    newStage = TenantStatus.Suspended;
                    newPayment = PaymentStatus.Delinquent;
                }
                else if (days >= _config.DegradedDays && record.CurrentStage == TenantStatus.Warning)
                {
                    newStage = TenantStatus.Degraded;
                }

                if (newStage is null)
                    continue;

                record.CurrentStage = newStage.Value;
                record.EscalatedAt = now;
                await dunningStore.UpsertAsync(record, ct);
                await tenantStore.UpdateStatusAsync(record.TenantId, newStage.Value, ct);

                if (newPayment is not null)
                {
                    var invoice = await invoiceStore.GetByIdAsync(
                        new TenantId(record.TenantId),
                        EntityId.From(record.InvoiceId),
                        ct);

                    if (invoice is not null)
                    {
                        invoice.PaymentStatus = newPayment.Value;
                        await invoiceStore.SaveAsync(invoice, ct);
                    }
                }

                // Dispatch lifecycle handler for Suspended (cleans up Realtime rows)
                if (newStage == TenantStatus.Suspended)
                {
                    foreach (var handler in lifecycleHandlers)
                    {
                        try { await handler.OnTenantSuspendedAsync(record.TenantId, ct); }
                        catch (Exception hex)
                        {
                            LogLifecycleHandlerFailed(_logger, record.TenantId, hex);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogEscalationFailed(_logger, record.TenantId, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Dunning cycle failed")]
    private static partial void LogDunningCycleFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to create dunning for invoice {InvoiceId}")]
    private static partial void LogDunningCreateFailed(ILogger logger, string invoiceId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dunning escalation failed for tenant {TenantId}")]
    private static partial void LogEscalationFailed(ILogger logger, string tenantId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lifecycle handler failed during dunning suspension for {TenantId}")]
    private static partial void LogLifecycleHandlerFailed(ILogger logger, string tenantId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.dunning — skipping cycle")]
    private static partial void LogCircuitOpen(ILogger logger);
}
