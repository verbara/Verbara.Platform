using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing.Tests;

public class InvoiceTests
{
    [Fact]
    public void Invoice_ShouldExposeAllProperties_WhenConstructed()
    {
        var id = EntityId.New();
        var tenantId = new TenantId("t1");
        var now = DateTimeOffset.UtcNow;
        var lineItems = new List<InvoiceLineItem>
        {
            new()
            {
                UsageType = UsageType.VoiceInbound,
                Description = "Voice Inbound",
                Quantity = 100m,
                UnitPrice = 0.05m,
                Amount = 5.00m,
            },
        };

        var invoice = new Invoice
        {
            InvoiceId = id,
            TenantId = tenantId,
            PeriodStart = now.AddDays(-30),
            PeriodEnd = now,
            Currency = "USD",
            LineItems = lineItems,
            Subtotal = 5.00m,
            Tax = 0.50m,
            Total = 5.50m,
            GeneratedAt = now,
        };

        invoice.InvoiceId.Should().Be(id);
        invoice.TenantId.Should().Be(tenantId);
        invoice.Currency.Should().Be("USD");
        invoice.LineItems.Should().HaveCount(1);
        invoice.Subtotal.Should().Be(5.00m);
        invoice.Tax.Should().Be(0.50m);
        invoice.Total.Should().Be(5.50m);
        invoice.Status.Should().Be(InvoiceStatus.Draft);
        invoice.IssuedAt.Should().BeNull();
        invoice.PaidAt.Should().BeNull();
    }

    [Fact]
    public void Invoice_ShouldImplementITenantScoped()
    {
#pragma warning disable CA1859
        ITenantScoped scoped = new Invoice
        {
            InvoiceId = EntityId.New(),
            TenantId = new TenantId("t1"),
            PeriodStart = DateTimeOffset.UtcNow,
            PeriodEnd = DateTimeOffset.UtcNow,
            Currency = "USD",
            LineItems = [],
            Subtotal = 0m,
            Total = 0m,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
#pragma warning restore CA1859

        scoped.TenantId.Should().Be(new TenantId("t1"));
    }

    [Fact]
    public void InvoiceStatus_ShouldHaveFourValues()
    {
        Enum.GetValues<InvoiceStatus>().Should().HaveCount(4);
        ((int)InvoiceStatus.Draft).Should().Be(0);
        ((int)InvoiceStatus.Issued).Should().Be(1);
        ((int)InvoiceStatus.Paid).Should().Be(2);
        ((int)InvoiceStatus.Void).Should().Be(3);
    }

    [Fact]
    public void InvoiceLineItem_ShouldDefaultIncludedAndOverageToZero()
    {
        var item = new InvoiceLineItem
        {
            UsageType = UsageType.SmsOutbound,
            Description = "SMS Outbound",
            Quantity = 50m,
            UnitPrice = 0.02m,
            Amount = 1.00m,
        };

        item.IncludedQuantity.Should().Be(0m);
        item.OverageQuantity.Should().Be(0m);
    }

    [Fact]
    public void Invoice_ShouldAllowStatusTransitions()
    {
        var invoice = new Invoice
        {
            InvoiceId = EntityId.New(),
            TenantId = new TenantId("t1"),
            PeriodStart = DateTimeOffset.UtcNow,
            PeriodEnd = DateTimeOffset.UtcNow,
            Currency = "USD",
            LineItems = [],
            Subtotal = 0m,
            Total = 0m,
            GeneratedAt = DateTimeOffset.UtcNow,
        };

        invoice.Status = InvoiceStatus.Issued;
        invoice.IssuedAt = DateTimeOffset.UtcNow;
        invoice.Status.Should().Be(InvoiceStatus.Issued);
        invoice.IssuedAt.Should().NotBeNull();

        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidAt = DateTimeOffset.UtcNow;
        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.PaidAt.Should().NotBeNull();
    }
}
