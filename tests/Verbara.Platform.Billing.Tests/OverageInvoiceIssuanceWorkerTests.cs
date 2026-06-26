using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Billing.Tests;

public class OverageInvoiceIssuanceWorkerTests
{
    private static readonly DateTimeOffset PeriodEnd = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private static OverageInvoiceIssuanceWorker Build(
        IInvoiceStore invoiceStore,
        IClock clock,
        DunningConfig? config = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(invoiceStore);
        services.AddSingleton(clock);

        var sp = services.BuildServiceProvider();
        return new OverageInvoiceIssuanceWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OverageInvoiceIssuanceWorker>.Instance,
            Options.Create(config ?? new DunningConfig()));
    }

    private static Invoice MakeDraftInvoice(
        string tenantId,
        DateTimeOffset periodEnd,
        decimal overageQuantity) => new()
    {
        InvoiceId = EntityId.New(),
        TenantId = new TenantId(tenantId),
        PeriodStart = periodEnd.AddMonths(-1),
        PeriodEnd = periodEnd,
        Currency = "USD",
        LineItems =
        [
            new InvoiceLineItem
            {
                UsageType = UsageType.AiAnalysis,
                Description = "AI analysis",
                Quantity = 1350m,
                UnitPrice = 0.02m,
                Amount = overageQuantity * 0.02m,
                IncludedQuantity = 1000m,
                OverageQuantity = overageQuantity,
            },
        ],
        Subtotal = overageQuantity * 0.02m,
        Total = overageQuantity * 0.02m,
        GeneratedAt = periodEnd,
        Status = InvoiceStatus.Draft,
    };

    [Fact]
    public async Task ProcessIssuanceCycle_ShouldLeaveDraft_WhenGraceNotElapsed()
    {
        var invoiceStore = Substitute.For<IInvoiceStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(PeriodEnd.AddDays(2));

        var invoice = MakeDraftInvoice("tenant-1", PeriodEnd, overageQuantity: 350m);
        invoiceStore.ListByStatusAsync(InvoiceStatus.Draft, Arg.Any<CancellationToken>())
            .Returns([invoice]);

        var worker = Build(invoiceStore, clock, new DunningConfig { OverageGraceDays = 3 });
        await worker.ProcessIssuanceCycleAsync(CancellationToken.None);

        invoice.Status.Should().Be(InvoiceStatus.Draft);
        invoice.DueDate.Should().BeNull();
        await invoiceStore.DidNotReceive().SaveAsync(Arg.Any<Invoice>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessIssuanceCycle_ShouldIssueWithDueDate_WhenGraceElapsed()
    {
        var invoiceStore = Substitute.For<IInvoiceStore>();
        var clock = Substitute.For<IClock>();
        var now = PeriodEnd.AddDays(4);
        clock.UtcNow.Returns(now);

        var invoice = MakeDraftInvoice("tenant-2", PeriodEnd, overageQuantity: 350m);
        invoiceStore.ListByStatusAsync(InvoiceStatus.Draft, Arg.Any<CancellationToken>())
            .Returns([invoice]);

        var worker = Build(invoiceStore, clock, new DunningConfig { OverageGraceDays = 3, PaymentTermDays = 14 });
        await worker.ProcessIssuanceCycleAsync(CancellationToken.None);

        invoice.Status.Should().Be(InvoiceStatus.Issued);
        invoice.IssuedAt.Should().Be(now);
        invoice.DueDate.Should().Be(now.AddDays(14));
        await invoiceStore.Received(1).SaveAsync(invoice, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessIssuanceCycle_ShouldLeaveDraft_WhenNoOverageLineItem()
    {
        var invoiceStore = Substitute.For<IInvoiceStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(PeriodEnd.AddDays(10));

        var invoice = MakeDraftInvoice("tenant-3", PeriodEnd, overageQuantity: 0m);
        invoiceStore.ListByStatusAsync(InvoiceStatus.Draft, Arg.Any<CancellationToken>())
            .Returns([invoice]);

        var worker = Build(invoiceStore, clock, new DunningConfig { OverageGraceDays = 3 });
        await worker.ProcessIssuanceCycleAsync(CancellationToken.None);

        invoice.Status.Should().Be(InvoiceStatus.Draft);
        invoice.DueDate.Should().BeNull();
        await invoiceStore.DidNotReceive().SaveAsync(Arg.Any<Invoice>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessIssuanceCycle_ShouldNotBlockOnSingleInvoiceFailure()
    {
        var invoiceStore = Substitute.For<IInvoiceStore>();
        var clock = Substitute.For<IClock>();
        var now = PeriodEnd.AddDays(10);
        clock.UtcNow.Returns(now);

        var failing = MakeDraftInvoice("tenant-fail", PeriodEnd, overageQuantity: 350m);
        var ok = MakeDraftInvoice("tenant-ok", PeriodEnd, overageQuantity: 200m);
        invoiceStore.ListByStatusAsync(InvoiceStatus.Draft, Arg.Any<CancellationToken>())
            .Returns([failing, ok]);

        invoiceStore.SaveAsync(failing, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("store error")));

        var worker = Build(invoiceStore, clock, new DunningConfig { OverageGraceDays = 3, PaymentTermDays = 14 });
        var act = () => worker.ProcessIssuanceCycleAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        ok.Status.Should().Be(InvoiceStatus.Issued);
        ok.DueDate.Should().Be(now.AddDays(14));
        await invoiceStore.Received(1).SaveAsync(ok, Arg.Any<CancellationToken>());
    }
}
