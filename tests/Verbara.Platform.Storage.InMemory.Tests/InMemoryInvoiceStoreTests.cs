using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Storage.InMemory.Tests;

public class InMemoryInvoiceStoreTests
{
    private readonly InMemoryInvoiceStore _store = new();
    private static readonly TenantId Tenant1 = new("t1");
    private static readonly TenantId Tenant2 = new("t2");

    private static Invoice MakeInvoice(TenantId tenantId, DateTimeOffset periodStart)
        => new()
        {
            InvoiceId = EntityId.New(),
            TenantId = tenantId,
            PeriodStart = periodStart,
            PeriodEnd = periodStart.AddMonths(1),
            Currency = "USD",
            LineItems = new List<InvoiceLineItem>
            {
                new()
                {
                    UsageType = UsageType.VoiceInbound,
                    Description = "Voice Inbound",
                    Quantity = 100m,
                    UnitPrice = 0.05m,
                    Amount = 5.00m,
                },
            },
            Subtotal = 5.00m,
            Total = 5.00m,
            GeneratedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task SaveAsync_ShouldPersist_AndGetByIdAsync_ShouldRetrieve()
    {
        var invoice = MakeInvoice(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(invoice, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant1, invoice.InvoiceId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.InvoiceId.Should().Be(invoice.InvoiceId);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotFound()
    {
        var result = await _store.GetByIdAsync(Tenant1, EntityId.New(), CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ShouldNotCrossTenants()
    {
        var invoice = MakeInvoice(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(invoice, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant2, invoice.InvoiceId, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnPaginatedResults()
    {
        for (var i = 0; i < 5; i++)
            await _store.SaveAsync(MakeInvoice(Tenant1, DateTimeOffset.UtcNow.AddMonths(-i)), CancellationToken.None);

        var page1 = await _store.ListAsync(Tenant1, 1, 2, CancellationToken.None);
        var page2 = await _store.ListAsync(Tenant1, 2, 2, CancellationToken.None);
        var page3 = await _store.ListAsync(Tenant1, 3, 2, CancellationToken.None);

        page1.Should().HaveCount(2);
        page2.Should().HaveCount(2);
        page3.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListAsync_ShouldOrderByPeriodStartDescending()
    {
        var jan = MakeInvoice(Tenant1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var mar = MakeInvoice(Tenant1, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var feb = MakeInvoice(Tenant1, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(jan, CancellationToken.None);
        await _store.SaveAsync(mar, CancellationToken.None);
        await _store.SaveAsync(feb, CancellationToken.None);

        var result = await _store.ListAsync(Tenant1, 1, 10, CancellationToken.None);

        result[0].PeriodStart.Month.Should().Be(3);
        result[1].PeriodStart.Month.Should().Be(2);
        result[2].PeriodStart.Month.Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_ShouldNotCrossTenants()
    {
        await _store.SaveAsync(MakeInvoice(Tenant1, DateTimeOffset.UtcNow), CancellationToken.None);
        await _store.SaveAsync(MakeInvoice(Tenant2, DateTimeOffset.UtcNow), CancellationToken.None);

        var result = await _store.ListAsync(Tenant1, 1, 10, CancellationToken.None);
        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldChangeStatus()
    {
        var invoice = MakeInvoice(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(invoice, CancellationToken.None);

        await _store.UpdateStatusAsync(Tenant1, invoice.InvoiceId, InvoiceStatus.Issued, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant1, invoice.InvoiceId, CancellationToken.None);
        result!.Status.Should().Be(InvoiceStatus.Issued);
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldBeNoOp_WhenNotFound()
    {
        // Should not throw
        await _store.UpdateStatusAsync(Tenant1, EntityId.New(), InvoiceStatus.Void, CancellationToken.None);
    }
}
