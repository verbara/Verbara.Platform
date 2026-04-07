using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Billing.Tests;

public class DunningServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 4, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DunningConfig DefaultConfig = new();

    private static (DunningService Service, IServiceProvider Provider) Build(
        IInvoiceStore invoiceStore,
        IDunningStore dunningStore,
        ITenantStore tenantStore,
        IClock clock,
        IEnumerable<ITenantLifecycleHandler>? handlers = null,
        DunningConfig? config = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(invoiceStore);
        services.AddSingleton(dunningStore);
        services.AddSingleton(tenantStore);
        services.AddSingleton(clock);

        foreach (var handler in handlers ?? [])
            services.AddSingleton<ITenantLifecycleHandler>(handler);

        var sp = services.BuildServiceProvider();
        var svc = new DunningService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DunningService>.Instance,
            Options.Create(config ?? DefaultConfig));

        return (svc, sp);
    }

    private static Invoice MakeIssuedInvoice(string tenantId, DateTimeOffset dueDate) => new()
    {
        InvoiceId = EntityId.New(),
        TenantId = new TenantId(tenantId),
        PeriodStart = FixedNow.AddMonths(-1),
        PeriodEnd = FixedNow,
        Currency = "USD",
        LineItems = [],
        Subtotal = 100m,
        Total = 100m,
        GeneratedAt = FixedNow.AddDays(-35),
        IssuedAt = FixedNow.AddDays(-32),
        DueDate = dueDate,
        Status = InvoiceStatus.Issued,
    };

    private static DunningRecord MakeActiveRecord(
        string tenantId,
        string invoiceId,
        TenantStatus stage,
        double ageInDays,
        bool isPaused = false) => new()
    {
        DunningId = EntityId.New().Value,
        TenantId = tenantId,
        InvoiceId = invoiceId,
        CurrentStage = stage,
        StartedAt = FixedNow.AddDays(-ageInDays),
        IsActive = true,
        IsPaused = isPaused,
    };

    [Fact]
    public async Task ProcessDunningCycle_ShouldCreateDunningRecord_WhenInvoiceOverdue()
    {
        var invoiceStore = Substitute.For<IInvoiceStore>();
        var dunningStore = Substitute.For<IDunningStore>();
        var tenantStore = Substitute.For<ITenantStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var invoice = MakeIssuedInvoice("tenant-1", FixedNow.AddDays(-3));
        invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, Arg.Any<CancellationToken>())
            .Returns([invoice]);
        dunningStore.GetByInvoiceAsync(invoice.InvoiceId.Value, Arg.Any<CancellationToken>())
            .Returns((DunningRecord?)null);
        dunningStore.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns([]);

        var (svc, _) = Build(invoiceStore, dunningStore, tenantStore, clock);
        await svc.ProcessDunningCycleAsync(CancellationToken.None);

        await dunningStore.Received(1).UpsertAsync(
            Arg.Is<DunningRecord>(r => r.TenantId == "tenant-1" && r.CurrentStage == TenantStatus.Warning),
            Arg.Any<CancellationToken>());
        await tenantStore.Received(1).UpdateStatusAsync("tenant-1", TenantStatus.Warning, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDunningCycle_ShouldEscalateToDegraded_WhenPast7Days()
    {
        var invoiceStore = Substitute.For<IInvoiceStore>();
        var dunningStore = Substitute.For<IDunningStore>();
        var tenantStore = Substitute.For<ITenantStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, Arg.Any<CancellationToken>())
            .Returns([]);

        var record = MakeActiveRecord("tenant-2", EntityId.New().Value, TenantStatus.Warning, ageInDays: 8);
        dunningStore.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns([record]);

        var (svc, _) = Build(invoiceStore, dunningStore, tenantStore, clock);
        await svc.ProcessDunningCycleAsync(CancellationToken.None);

        await dunningStore.Received(1).UpsertAsync(
            Arg.Is<DunningRecord>(r => r.TenantId == "tenant-2" && r.CurrentStage == TenantStatus.Degraded),
            Arg.Any<CancellationToken>());
        await tenantStore.Received(1).UpdateStatusAsync("tenant-2", TenantStatus.Degraded, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDunningCycle_ShouldEscalateToSuspended_WhenPast14Days()
    {
        var invoiceStore = Substitute.For<IInvoiceStore>();
        var dunningStore = Substitute.For<IDunningStore>();
        var tenantStore = Substitute.For<ITenantStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, Arg.Any<CancellationToken>())
            .Returns([]);

        var invoiceId = EntityId.New().Value;
        var record = MakeActiveRecord("tenant-3", invoiceId, TenantStatus.Degraded, ageInDays: 15);
        dunningStore.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns([record]);

        var invoice = MakeIssuedInvoice("tenant-3", FixedNow.AddDays(-15));
        invoiceStore.GetByIdAsync(
                Arg.Is<TenantId>(t => t.Value == "tenant-3"),
                Arg.Is<EntityId>(e => e.Value == invoiceId),
                Arg.Any<CancellationToken>())
            .Returns(invoice);

        var (svc, _) = Build(invoiceStore, dunningStore, tenantStore, clock);
        await svc.ProcessDunningCycleAsync(CancellationToken.None);

        await dunningStore.Received(1).UpsertAsync(
            Arg.Is<DunningRecord>(r => r.TenantId == "tenant-3" && r.CurrentStage == TenantStatus.Suspended),
            Arg.Any<CancellationToken>());
        await tenantStore.Received(1).UpdateStatusAsync("tenant-3", TenantStatus.Suspended, Arg.Any<CancellationToken>());
        invoice.PaymentStatus.Should().Be(PaymentStatus.Delinquent);
    }

    [Fact]
    public async Task ProcessDunningCycle_ShouldEscalateToPendingDeletion_WhenPast30Days()
    {
        var invoiceStore = Substitute.For<IInvoiceStore>();
        var dunningStore = Substitute.For<IDunningStore>();
        var tenantStore = Substitute.For<ITenantStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, Arg.Any<CancellationToken>())
            .Returns([]);

        var invoiceId = EntityId.New().Value;
        var record = MakeActiveRecord("tenant-4", invoiceId, TenantStatus.Suspended, ageInDays: 31);
        dunningStore.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns([record]);

        var invoice = MakeIssuedInvoice("tenant-4", FixedNow.AddDays(-31));
        invoiceStore.GetByIdAsync(
                Arg.Is<TenantId>(t => t.Value == "tenant-4"),
                Arg.Is<EntityId>(e => e.Value == invoiceId),
                Arg.Any<CancellationToken>())
            .Returns(invoice);

        var (svc, _) = Build(invoiceStore, dunningStore, tenantStore, clock);
        await svc.ProcessDunningCycleAsync(CancellationToken.None);

        await dunningStore.Received(1).UpsertAsync(
            Arg.Is<DunningRecord>(r => r.TenantId == "tenant-4" && r.CurrentStage == TenantStatus.PendingDeletion),
            Arg.Any<CancellationToken>());
        await tenantStore.Received(1).UpdateStatusAsync("tenant-4", TenantStatus.PendingDeletion, Arg.Any<CancellationToken>());
        invoice.PaymentStatus.Should().Be(PaymentStatus.WrittenOff);
    }

    [Fact]
    public async Task ProcessDunningCycle_ShouldSkipPausedRecords()
    {
        var invoiceStore = Substitute.For<IInvoiceStore>();
        var dunningStore = Substitute.For<IDunningStore>();
        var tenantStore = Substitute.For<ITenantStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, Arg.Any<CancellationToken>())
            .Returns([]);

        var record = MakeActiveRecord("tenant-5", EntityId.New().Value, TenantStatus.Warning, ageInDays: 10, isPaused: true);
        dunningStore.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns([record]);

        var (svc, _) = Build(invoiceStore, dunningStore, tenantStore, clock);
        await svc.ProcessDunningCycleAsync(CancellationToken.None);

        await dunningStore.DidNotReceive().UpsertAsync(Arg.Any<DunningRecord>(), Arg.Any<CancellationToken>());
        await tenantStore.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<TenantStatus>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDunningCycle_ShouldNotBlockOnSingleTenantFailure()
    {
        var invoiceStore = Substitute.For<IInvoiceStore>();
        var dunningStore = Substitute.For<IDunningStore>();
        var tenantStore = Substitute.For<ITenantStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, Arg.Any<CancellationToken>())
            .Returns([]);

        var record1 = MakeActiveRecord("tenant-fail", EntityId.New().Value, TenantStatus.Warning, ageInDays: 8);
        var record2 = MakeActiveRecord("tenant-ok", EntityId.New().Value, TenantStatus.Warning, ageInDays: 8);
        dunningStore.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns([record1, record2]);

#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        tenantStore.UpdateStatusAsync("tenant-fail", Arg.Any<TenantStatus>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new ValueTask(Task.FromException(new InvalidOperationException("store error"))));
#pragma warning restore CA2012

        var (svc, _) = Build(invoiceStore, dunningStore, tenantStore, clock);
        var act = () => svc.ProcessDunningCycleAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await tenantStore.Received(1).UpdateStatusAsync("tenant-ok", TenantStatus.Degraded, Arg.Any<CancellationToken>());
    }
}
