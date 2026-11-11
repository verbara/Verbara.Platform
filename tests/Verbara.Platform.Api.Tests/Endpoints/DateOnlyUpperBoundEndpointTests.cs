using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Verbara.Platform.Audit;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Impersonation;
using Verbara.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// A date-only upper bound (<c>to</c>, <c>until</c>, <c>endDate</c> = <c>yyyy-MM-dd</c>) includes that whole
/// UTC day on every endpoint that reads a date range from the query string, not only on the analytics ones.
/// Each test stamps a row late on the chosen day (23:30 UTC) and asks for that day as the upper bound.
/// </summary>
internal static class DateOnlyBound
{
    public static readonly DateTimeOffset Day = new(2026, 3, 4, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset LateOnDay = Day.AddHours(23).AddMinutes(30);
    public const string DayText = "2026-03-04";

    public static string UtcToday => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public sealed class AuditDateOnlyUpperBoundTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    public AuditDateOnlyUpperBoundTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task SearchAuditLog_ShouldIncludeTheWholeDay_WhenToIsDateOnly()
    {
        await SeedAsync("legacy-late.row", DateOnlyBound.LateOnDay);

        var response = await _client.GetAsync(
            $"/api/v1/admin/audit?action=legacy-late.row&from={DateOnlyBound.DayText}&to={DateOnlyBound.DayText}&pageSize=200");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("legacy-late.row");
    }

    [Fact]
    public async Task QueryEvents_ShouldIncludeTheWholeDay_WhenToIsDateOnly()
    {
        await SeedAsync("viewer-late.row", DateOnlyBound.LateOnDay);

        var response = await _client.GetAsync(
            $"/api/v1/admin/audit/events?actionPrefix=viewer-late.&from={DateOnlyBound.DayText}&to={DateOnlyBound.DayText}&pageSize=200");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("viewer-late.row");
    }

    [Fact]
    public async Task ExportEvents_ShouldIncludeTheWholeDay_WhenToIsDateOnly()
    {
        await SeedAsync("export-late.row", DateOnlyBound.LateOnDay);

        var response = await _client.GetAsync(
            $"/api/v1/admin/audit/export?format=json&actionPrefix=export-late.&to={DateOnlyBound.DayText}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("export-late.row");
    }

    [Fact]
    public async Task QueryEvents_ShouldExcludeTheRestOfTheDay_WhenToIsAMidnightInstant()
    {
        await SeedAsync("viewer-instant.row", DateOnlyBound.LateOnDay);

        var response = await _client.GetAsync(
            $"/api/v1/admin/audit/events?actionPrefix=viewer-instant.&to={DateOnlyBound.DayText}T00:00:00Z&pageSize=200");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("viewer-instant.row");
    }

    [Theory]
    [InlineData("/api/v1/admin/audit?to=not-a-date")]
    [InlineData("/api/v1/admin/audit/events?from=not-a-date")]
    [InlineData("/api/v1/admin/audit/export?to=not-a-date")]
    public async Task AuditEndpoints_ShouldReturn400_WhenABoundIsMalformed(string url)
    {
        var response = await _client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task SeedAsync(string action, DateTimeOffset occurredAt)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        await store.SaveAsync(new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = new TenantId(PlatformAdminApiFactory.HostTenantId),
            Action = action,
            TargetType = "date-bound",
            Category = "admin",
            Severity = "info",
            ActorId = "date-bound-actor",
            ActorType = "user",
            OccurredAt = occurredAt,
        }, CancellationToken.None);
    }
}

public sealed class PurgeLogDateOnlyUpperBoundTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    public PurgeLogDateOnlyUpperBoundTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task ListPurgeLog_ShouldIncludeTheWholeDay_WhenToIsDateOnly()
    {
        var store = _factory.Services.GetRequiredService<IPurgeLogStore>();
        await store.SaveAsync(new PurgeEntry
        {
            PurgeId = "purge-late-on-day",
            TenantId = "purge-date-bound-tenant",
            SubjectType = "contact",
            SubjectId = "subject-1",
            PerformedBy = "admin",
            Reason = "request",
            EntitiesDeleted = new Dictionary<string, int> { ["contacts"] = 1 },
            PurgedAt = DateOnlyBound.LateOnDay,
        }, CancellationToken.None);

        var response = await _client.GetAsync(
            $"/api/management/gdpr/purge-log?tenantId=purge-date-bound-tenant&from={DateOnlyBound.DayText}&to={DateOnlyBound.DayText}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("purge-late-on-day");
    }

    [Fact]
    public async Task ListPurgeLog_ShouldReturn400_WhenToIsMalformed()
    {
        var response = await _client.GetAsync("/api/management/gdpr/purge-log?to=not-a-date");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

public sealed class ImpersonationHistoryDateOnlyUpperBoundTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    public ImpersonationHistoryDateOnlyUpperBoundTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task ListSessionHistory_ShouldIncludeTheWholeDay_WhenToIsDateOnly()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IImpersonationSessionStore>();
            await store.AddAsync(new ImpersonationSession
            {
                Id = "history-late-on-day",
                TokenId = "jti-history-late-on-day",
                TokenExpiresAt = DateOnlyBound.LateOnDay.AddMinutes(20),
                ActorUserId = PlatformAdminApiFactory.TestPlatformAdminUserId,
                ActorTenantId = PlatformAdminApiFactory.HostTenantId,
                TargetTenantId = "tenant-acme",
                ReadOnly = false,
                StartedAt = DateOnlyBound.LateOnDay,
                Status = ImpersonationSessionStatus.Completed,
                CloseReason = "completed",
                EndedAt = DateOnlyBound.LateOnDay.AddMinutes(10),
            }, CancellationToken.None);
        }

        var response = await _client.GetAsync(
            $"/api/v1/management/impersonation/sessions/history?from={DateOnlyBound.DayText}&to={DateOnlyBound.DayText}&pageSize=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("history-late-on-day");
    }
}

public sealed class ManagementUsageDateOnlyUpperBoundTests : IClassFixture<PlatformAdminApiFactory>
{
    private const string TenantId = "usage-date-bound-tenant";
    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    public ManagementUsageDateOnlyUpperBoundTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task GetUsageDetailsAndSummary_ShouldIncludeTheWholeDay_WhenUntilIsDateOnly()
    {
        var store = _factory.Services.GetRequiredService<IUsageRecordStore>();
        await store.SaveAsync(new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = new TenantId(TenantId),
            UsageType = UsageType.VoiceInbound,
            Quantity = 7m,
            Unit = UsageUnit.Minutes,
            RecordedAt = DateOnlyBound.LateOnDay,
        }, CancellationToken.None);

        var range = $"from={DateOnlyBound.DayText}&until={DateOnlyBound.DayText}";
        var details = await _client.GetAsync($"/api/management/tenants/{TenantId}/usage/details?{range}");
        var summary = await _client.GetAsync($"/api/management/tenants/{TenantId}/usage?{range}");

        details.StatusCode.Should().Be(HttpStatusCode.OK);
        (await details.Content.ReadAsStringAsync()).Should().Contain("VoiceInbound");
        summary.StatusCode.Should().Be(HttpStatusCode.OK);
        (await summary.Content.ReadAsStringAsync()).Should().Contain("VoiceInbound");
    }

    [Fact]
    public async Task GetUsageDetails_ShouldReturn400_WhenUntilIsMalformed()
    {
        var response = await _client.GetAsync($"/api/management/tenants/{TenantId}/usage/details?until=not-a-date");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

public sealed class PartnerDateOnlyUpperBoundTests : IClassFixture<PartnerApiFactory>
{
    private readonly PartnerApiFactory _factory;
    private readonly HttpClient _client;

    public PartnerDateOnlyUpperBoundTests(PartnerApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePartnerClient();
    }

    [Fact]
    public async Task GetRevenueDetails_ShouldIncludeTheWholeDay_WhenUntilIsDateOnly()
    {
        var revenueId = EntityId.New();
        await _factory.RevenueStore.UpsertAsync(new PartnerRevenueRecord
        {
            RevenueId = revenueId,
            PartnerTenantId = new TenantId(PartnerApiFactory.PartnerTenantId),
            CustomerTenantId = new TenantId(PartnerApiFactory.CustomerTenantId),
            InvoiceId = EntityId.New(),
            GrossAmount = 10m,
            PlatformCost = 6m,
            PartnerMargin = 4m,
            PeriodStart = DateOnlyBound.LateOnDay,
            PeriodEnd = DateOnlyBound.LateOnDay.AddDays(30),
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var response = await _client.GetAsync(
            $"/api/v1/partner/revenue/details?from={DateOnlyBound.DayText}&until={DateOnlyBound.DayText}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(revenueId.Value);
    }

    [Fact]
    public async Task GenerateCustomerInvoice_ShouldBillTheWholeLastDay_WhenToIsDateOnly()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (tenant, price) in new[]
                 {
                     (PartnerApiFactory.HostTenantId, 0.005m),
                     (PartnerApiFactory.PartnerTenantId, 0.01m),
                 })
        {
            await _factory.RateCardStore.SaveAsync(new RateCard
            {
                RateCardId = EntityId.New(),
                TenantId = new TenantId(tenant),
                Name = "Date bound card",
                Currency = "USD",
                EffectiveFrom = now.AddDays(-30),
                IsDefault = true,
                Rates = [new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = price }],
            }, CancellationToken.None);
        }

        var usage = _factory.Services.GetRequiredService<IUsageRecordStore>();
        await usage.SaveAsync(new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = new TenantId(PartnerApiFactory.CustomerTenantId),
            UsageType = UsageType.VoiceInbound,
            Quantity = 100m,
            Unit = UsageUnit.Minutes,
            RecordedAt = DateOnlyBound.LateOnDay,
        }, CancellationToken.None);

        var response = await _client.PostAsync(
            $"/api/v1/partner/customers/{PartnerApiFactory.CustomerTenantId}/invoices/generate?from={DateOnlyBound.DayText}&to={DateOnlyBound.DayText}",
            null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var invoice = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["invoice"]!;
        invoice["total"]!.GetValue<decimal>().Should().BeGreaterThan(0m);
        invoice["periodStart"]!.GetValue<DateTimeOffset>().Should().Be(DateOnlyBound.Day);
        invoice["periodEnd"]!.GetValue<DateTimeOffset>().Should().Be(DateOnlyBound.Day.AddDays(1));
    }

    [Fact]
    public async Task GetPartnerAttribution_ShouldIncludeTheWholeDay_WhenToIsDateOnly()
    {
        using var factory = new PartnerApiFactory();
        using var client = factory.CreatePartnerClient();
        var customerId = PartnerApiFactory.CustomerTenantId;
        using (var scope = factory.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<ICreditLedgerStore>();
            await ledger.PostGrantAsync(new CreditLedgerEntry
            {
                EntryId = EntityId.New(),
                TenantId = new TenantId(customerId),
                EntryType = CreditEntryType.Grant,
                Source = CreditSource.Partner,
                Amount = 1000m,
                ExternalRef = "date-bound-grant",
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
            // The debit is stamped now, so it falls after today's 00:00 UTC.
            await ledger.PostMeteredDebitAsync(new TenantId(customerId), 600m, "date-bound-usage", CancellationToken.None);
        }

        var today = DateOnlyBound.UtcToday;
        var response = await client.GetAsync($"/api/partner/credit-ledger/attribution?from={today}&to={today}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["total"]!.GetValue<decimal>().Should().Be(600m);
    }

    [Theory]
    [InlineData("/api/v1/partner/revenue/?until=not-a-date")]
    [InlineData("/api/partner/credit-ledger/attribution?to=not-a-date")]
    public async Task PartnerEndpoints_ShouldReturn400_WhenABoundIsMalformed(string url)
    {
        var response = await _client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

public sealed class AuthEventsDateOnlyUpperBoundTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public AuthEventsDateOnlyUpperBoundTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task ListEvents_ShouldIncludeTheWholeDay_WhenEndDateIsDateOnly()
    {
        await SeedAsync("auth-late-on-day", DateOnlyBound.LateOnDay);

        var response = await _client.GetAsync(
            $"/api/admin/auth/events?startDate={DateOnlyBound.DayText}&endDate={DateOnlyBound.DayText}&pageSize=200");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("auth-late-on-day");
    }

    [Fact]
    public async Task ListEvents_ShouldKeepTheInstant_WhenEndDateIsAMidnightInstant()
    {
        await SeedAsync("auth-after-midnight-instant", DateOnlyBound.Day.AddHours(12));

        var response = await _client.GetAsync(
            $"/api/admin/auth/events?startDate={DateOnlyBound.DayText}&endDate={DateOnlyBound.DayText}T00:00:00Z&pageSize=200");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("auth-after-midnight-instant");
    }

    [Fact]
    public async Task ListEvents_ShouldReturn400_WhenEndDateIsMalformed()
    {
        var response = await _client.GetAsync("/api/admin/auth/events?endDate=not-a-date");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task SeedAsync(string eventId, DateTimeOffset createdAt)
    {
        var store = _factory.Services.GetRequiredService<IAuthEventStore>();
        await store.SaveAsync(new AuthEvent
        {
            EventId = eventId,
            TenantId = AuthenticatedPlatformApiFactory.TestTenantId,
            EventType = "login_success",
            CreatedAt = createdAt,
        }, CancellationToken.None);
    }
}
