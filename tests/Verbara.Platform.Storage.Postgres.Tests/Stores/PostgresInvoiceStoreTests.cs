using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Round-trips <see cref="PostgresInvoiceStore"/> against a real Postgres DB via
/// Testcontainers so the due-date + payment-status persistence (migration 011) is
/// exercised end-to-end against the actual schema — the InMemory store cannot reach
/// the new SQL columns, the <c>(PaymentStatus)payment_status</c> ordinal cast, the
/// <c>GetDateTimeOrNull("due_date")</c> read, the <c>NpgsqlDbType.TimestampTz</c>
/// binding, or the <c>ON CONFLICT (invoice_id) DO UPDATE</c> upsert. Backs the
/// spec's <c>Invoice due-date and payment-status persistence</c> requirement.
/// </summary>
[Collection("InvoiceStore")]
public sealed class PostgresInvoiceStoreTests
{
    private readonly InvoiceStoreFixture _fixture;
    private static readonly TenantId Tenant1 = new("acme-invoice");
    private static readonly DateTimeOffset BaseTime = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    public PostgresInvoiceStoreTests(InvoiceStoreFixture fixture)
    {
        _fixture = fixture;
    }

    private static Invoice MakeInvoice(
        EntityId? id = null,
        TenantId? tenantId = null,
        DateTimeOffset? periodStart = null,
        InvoiceStatus status = InvoiceStatus.Draft) => new()
    {
        InvoiceId = id ?? EntityId.New(),
        TenantId = tenantId ?? Tenant1,
        PeriodStart = periodStart ?? BaseTime,
        PeriodEnd = (periodStart ?? BaseTime).AddMonths(1),
        Currency = "USD",
        LineItems = new List<InvoiceLineItem>
        {
            new()
            {
                UsageType = UsageType.AiAnalysis,
                Description = "AI Analysis overage",
                Quantity = 1350m,
                UnitPrice = 0.02m,
                Amount = 7.00m,
                IncludedQuantity = 1000m,
                OverageQuantity = 350m,
            },
        },
        Subtotal = 7.00m,
        Total = 7.00m,
        Status = status,
        GeneratedAt = BaseTime,
    };

    // Scenario: DueDate and PaymentStatus round-trip through Postgres (GetByIdAsync).
    [Fact]
    public async Task GetByIdAsync_ShouldRoundTripDueDateAndPaymentStatus_WhenSet()
    {
        await _fixture.ResetAsync();

        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = new PostgresInvoiceStore(dataSource);
            var due = new DateTimeOffset(2026, 4, 15, 9, 30, 0, TimeSpan.Zero);
            var invoice = MakeInvoice(status: InvoiceStatus.Issued);
            invoice.DueDate = due;
            invoice.PaymentStatus = PaymentStatus.Overdue;

            await store.SaveAsync(invoice, CancellationToken.None);

            var result = await store.GetByIdAsync(Tenant1, invoice.InvoiceId, CancellationToken.None);

            result.Should().NotBeNull();
            // Postgres TIMESTAMPTZ has microsecond precision — compare with a tolerance.
            result!.DueDate.Should().NotBeNull();
            result.DueDate!.Value.Should().BeCloseTo(due, TimeSpan.FromMilliseconds(1));
            result.PaymentStatus.Should().Be(PaymentStatus.Overdue);
        }
    }

    // Scenario: DueDate and PaymentStatus round-trip through Postgres (ListByStatusAsync).
    [Fact]
    public async Task ListByStatusAsync_ShouldRoundTripDueDateAndPaymentStatus_WhenSet()
    {
        await _fixture.ResetAsync();

        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = new PostgresInvoiceStore(dataSource);
            var due = new DateTimeOffset(2026, 4, 20, 0, 0, 0, TimeSpan.Zero);
            var invoice = MakeInvoice(status: InvoiceStatus.Issued);
            invoice.DueDate = due;
            invoice.PaymentStatus = PaymentStatus.Overdue;
            await store.SaveAsync(invoice, CancellationToken.None);

            // A Draft invoice must NOT appear in the Issued listing.
            await store.SaveAsync(MakeInvoice(status: InvoiceStatus.Draft), CancellationToken.None);

            var issued = await store.ListByStatusAsync(InvoiceStatus.Issued, CancellationToken.None);

            issued.Should().ContainSingle();
            var rehydrated = issued[0];
            rehydrated.InvoiceId.Should().Be(invoice.InvoiceId);
            rehydrated.DueDate.Should().NotBeNull();
            rehydrated.DueDate!.Value.Should().BeCloseTo(due, TimeSpan.FromMilliseconds(1));
            rehydrated.PaymentStatus.Should().Be(PaymentStatus.Overdue);
        }
    }

    // Scenario: Legacy rows without the columns default safely.
    // A row inserted in the pre-011 shape (no due_date / payment_status) is read back
    // after the migration ALTER with PaymentStatus=Current and DueDate=null.
    [Fact]
    public async Task GetByIdAsync_ShouldDefaultLegacyRow_WhenColumnsAbsentBeforeMigration()
    {
        var legacyId = EntityId.New();
        var seed = $"""
            INSERT INTO invoices (invoice_id, tenant_id, period_start, period_end, currency,
                line_items, subtotal, tax, total, status, generated_at, issued_at, paid_at)
            VALUES ('{legacyId.Value}', '{Tenant1.Value}',
                TIMESTAMPTZ '2026-03-01T00:00:00Z', TIMESTAMPTZ '2026-04-01T00:00:00Z', 'USD',
                '[]'::jsonb, 0, 0, 0, 0, TIMESTAMPTZ '2026-03-01T00:00:00Z', NULL, NULL);
            """;
        await _fixture.ResetAsync(seedLegacyRows: seed);

        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = new PostgresInvoiceStore(dataSource);

            var result = await store.GetByIdAsync(Tenant1, legacyId, CancellationToken.None);

            result.Should().NotBeNull();
            result!.PaymentStatus.Should().Be(PaymentStatus.Current);
            result.DueDate.Should().BeNull();
        }
    }

    // Scenario: PaymentStatus mutation survives a subsequent reload — exercises the
    // ON CONFLICT (invoice_id) DO UPDATE upsert. The prior INSERT-only store would
    // throw 23505 (duplicate PK) on the second SaveAsync; this asserts it does not.
    [Fact]
    public async Task SaveAsync_ShouldUpsertPaymentStatusMutation_WhenReSavedWithSamePrimaryKey()
    {
        await _fixture.ResetAsync();

        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var store = new PostgresInvoiceStore(dataSource);
            var invoice = MakeInvoice(status: InvoiceStatus.Issued);
            invoice.PaymentStatus = PaymentStatus.Current;
            invoice.DueDate = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero);
            await store.SaveAsync(invoice, CancellationToken.None);

            // A dunning cycle mutates PaymentStatus and re-saves the SAME primary key.
            invoice.PaymentStatus = PaymentStatus.Delinquent;

            // Must not throw 23505 (the INSERT-only store would have).
            var reSave = async () => await store.SaveAsync(invoice, CancellationToken.None);
            await reSave.Should().NotThrowAsync();

            var result = await store.GetByIdAsync(Tenant1, invoice.InvoiceId, CancellationToken.None);
            result.Should().NotBeNull();
            result!.PaymentStatus.Should().Be(PaymentStatus.Delinquent);
            // Other columns persisted by the upsert remain intact.
            result.DueDate.Should().NotBeNull();
        }
    }
}
