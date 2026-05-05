using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryAuthEventStoreSearchTests
{
    private const string Tenant1 = "tenant-1";
    private const string Tenant2 = "tenant-2";
    private const string User1 = "user-1";
    private const string User2 = "user-2";

    private static readonly DateTimeOffset BaseDate = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private static AuthEvent MakeEvent(
        string? tenantId = null,
        string? userId = null,
        string? eventType = null,
        DateTimeOffset? createdAt = null) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        TenantId = tenantId ?? Tenant1,
        UserId = userId ?? User1,
        EventType = eventType ?? AuthEventTypes.LoginSuccess,
        CreatedAt = createdAt ?? BaseDate,
    };

    [Fact]
    public async Task SearchAsync_ShouldReturnAllEvents_WhenNoFiltersApplied()
    {
        var store = new InMemoryAuthEventStore();
        await store.SaveAsync(MakeEvent(), CancellationToken.None);
        await store.SaveAsync(MakeEvent(eventType: AuthEventTypes.LoginFailure), CancellationToken.None);
        await store.SaveAsync(MakeEvent(eventType: AuthEventTypes.Logout), CancellationToken.None);

        var result = await store.SearchAsync(Tenant1, new AuthEventQuery(), CancellationToken.None);

        result.TotalCount.Should().Be(3);
        result.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterByEventType_WhenEventTypeProvided()
    {
        var store = new InMemoryAuthEventStore();
        await store.SaveAsync(MakeEvent(eventType: AuthEventTypes.LoginSuccess), CancellationToken.None);
        await store.SaveAsync(MakeEvent(eventType: AuthEventTypes.LoginSuccess), CancellationToken.None);
        await store.SaveAsync(MakeEvent(eventType: AuthEventTypes.LoginFailure), CancellationToken.None);
        await store.SaveAsync(MakeEvent(eventType: AuthEventTypes.Logout), CancellationToken.None);

        var result = await store.SearchAsync(Tenant1, new AuthEventQuery(EventType: AuthEventTypes.LoginSuccess), CancellationToken.None);

        result.TotalCount.Should().Be(2);
        result.Items.Should().AllSatisfy(e => e.EventType.Should().Be(AuthEventTypes.LoginSuccess));
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterByUserId_WhenUserIdProvided()
    {
        var store = new InMemoryAuthEventStore();
        await store.SaveAsync(MakeEvent(userId: User1), CancellationToken.None);
        await store.SaveAsync(MakeEvent(userId: User1), CancellationToken.None);
        await store.SaveAsync(MakeEvent(userId: User2), CancellationToken.None);

        var result = await store.SearchAsync(Tenant1, new AuthEventQuery(UserId: User1), CancellationToken.None);

        result.TotalCount.Should().Be(2);
        result.Items.Should().AllSatisfy(e => e.UserId.Should().Be(User1));
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterByStartDate_WhenStartDateProvided()
    {
        var store = new InMemoryAuthEventStore();
        var yesterday = BaseDate.AddDays(-1);
        var today = BaseDate;
        var tomorrow = BaseDate.AddDays(1);

        await store.SaveAsync(MakeEvent(createdAt: yesterday), CancellationToken.None);
        await store.SaveAsync(MakeEvent(createdAt: today), CancellationToken.None);
        await store.SaveAsync(MakeEvent(createdAt: tomorrow), CancellationToken.None);

        var result = await store.SearchAsync(Tenant1, new AuthEventQuery(StartDate: today), CancellationToken.None);

        result.TotalCount.Should().Be(2);
        result.Items.Should().AllSatisfy(e => e.CreatedAt.Should().BeOnOrAfter(today));
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterByEndDate_WhenEndDateProvided()
    {
        var store = new InMemoryAuthEventStore();
        var yesterday = BaseDate.AddDays(-1);
        var today = BaseDate;
        var tomorrow = BaseDate.AddDays(1);

        await store.SaveAsync(MakeEvent(createdAt: yesterday), CancellationToken.None);
        await store.SaveAsync(MakeEvent(createdAt: today), CancellationToken.None);
        await store.SaveAsync(MakeEvent(createdAt: tomorrow), CancellationToken.None);

        var result = await store.SearchAsync(Tenant1, new AuthEventQuery(EndDate: today), CancellationToken.None);

        result.TotalCount.Should().Be(2);
        result.Items.Should().AllSatisfy(e => e.CreatedAt.Should().BeOnOrBefore(today));
    }

    [Fact]
    public async Task SearchAsync_ShouldFilterByDateRange_WhenBothDatesProvided()
    {
        var store = new InMemoryAuthEventStore();
        var t1 = BaseDate.AddDays(-2);
        var t2 = BaseDate.AddDays(-1);
        var t3 = BaseDate;
        var t4 = BaseDate.AddDays(1);

        await store.SaveAsync(MakeEvent(createdAt: t1), CancellationToken.None);
        await store.SaveAsync(MakeEvent(createdAt: t2), CancellationToken.None);
        await store.SaveAsync(MakeEvent(createdAt: t3), CancellationToken.None);
        await store.SaveAsync(MakeEvent(createdAt: t4), CancellationToken.None);

        var result = await store.SearchAsync(Tenant1, new AuthEventQuery(StartDate: t2, EndDate: t3), CancellationToken.None);

        result.TotalCount.Should().Be(2);
        result.Items.Should().AllSatisfy(e =>
        {
            e.CreatedAt.Should().BeOnOrAfter(t2);
            e.CreatedAt.Should().BeOnOrBefore(t3);
        });
    }

    [Fact]
    public async Task SearchAsync_ShouldCombineFilters_WhenMultipleProvided()
    {
        var store = new InMemoryAuthEventStore();
        await store.SaveAsync(MakeEvent(userId: User1, eventType: AuthEventTypes.LoginSuccess), CancellationToken.None);
        await store.SaveAsync(MakeEvent(userId: User1, eventType: AuthEventTypes.LoginFailure), CancellationToken.None);
        await store.SaveAsync(MakeEvent(userId: User2, eventType: AuthEventTypes.LoginSuccess), CancellationToken.None);
        await store.SaveAsync(MakeEvent(userId: User2, eventType: AuthEventTypes.LoginFailure), CancellationToken.None);

        var result = await store.SearchAsync(Tenant1, new AuthEventQuery(UserId: User1, EventType: AuthEventTypes.LoginSuccess), CancellationToken.None);

        result.TotalCount.Should().Be(1);
        result.Items[0].UserId.Should().Be(User1);
        result.Items[0].EventType.Should().Be(AuthEventTypes.LoginSuccess);
    }

    [Fact]
    public async Task SearchAsync_ShouldPaginate_WhenPageAndPageSizeProvided()
    {
        var store = new InMemoryAuthEventStore();
        for (var i = 0; i < 10; i++)
            await store.SaveAsync(MakeEvent(createdAt: BaseDate.AddMinutes(i)), CancellationToken.None);

        var result = await store.SearchAsync(Tenant1, new AuthEventQuery(Page: 2, PageSize: 3), CancellationToken.None);

        result.Items.Should().HaveCount(3);
        result.TotalCount.Should().Be(10);
        result.Page.Should().Be(2);
        result.PageSize.Should().Be(3);
    }

    [Fact]
    public async Task SearchAsync_ShouldIsolateTenants_WhenDifferentTenantIdsUsed()
    {
        var store = new InMemoryAuthEventStore();
        await store.SaveAsync(MakeEvent(tenantId: Tenant1), CancellationToken.None);
        await store.SaveAsync(MakeEvent(tenantId: Tenant1), CancellationToken.None);
        await store.SaveAsync(MakeEvent(tenantId: Tenant2), CancellationToken.None);

        var r1 = await store.SearchAsync(Tenant1, new AuthEventQuery(), CancellationToken.None);
        var r2 = await store.SearchAsync(Tenant2, new AuthEventQuery(), CancellationToken.None);

        r1.TotalCount.Should().Be(2);
        r2.TotalCount.Should().Be(1);
        r1.Items.Should().AllSatisfy(e => e.TenantId.Should().Be(Tenant1));
        r2.Items.Should().AllSatisfy(e => e.TenantId.Should().Be(Tenant2));
    }

    [Fact]
    public async Task SearchAsync_ShouldOrderByCreatedAtDescending()
    {
        var store = new InMemoryAuthEventStore();
        var t1 = BaseDate.AddHours(1);
        var t2 = BaseDate.AddHours(3);
        var t3 = BaseDate.AddHours(2);

        await store.SaveAsync(MakeEvent(createdAt: t1), CancellationToken.None);
        await store.SaveAsync(MakeEvent(createdAt: t2), CancellationToken.None);
        await store.SaveAsync(MakeEvent(createdAt: t3), CancellationToken.None);

        var result = await store.SearchAsync(Tenant1, new AuthEventQuery(), CancellationToken.None);

        result.Items.Should().HaveCount(3);
        result.Items[0].CreatedAt.Should().Be(t2);
        result.Items[1].CreatedAt.Should().Be(t3);
        result.Items[2].CreatedAt.Should().Be(t1);
    }
}
