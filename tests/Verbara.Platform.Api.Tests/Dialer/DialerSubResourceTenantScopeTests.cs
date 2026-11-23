using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.Dialer.Scheduling;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-sub-resources-stay-in-their-tenant: the holiday routes of a holiday calendar and the entry routes of a
/// caller-ID pool act only on a calendar or pool of the caller's tenant, through the HTTP pipeline with two Customer
/// tenants (<c>acme</c>, <c>globex</c>) authenticated by real JWTs (<see cref="DialerTenantScopeFactory"/>). The stores
/// are the in-memory ones, whose calendar and pool lookups follow the tenant and whose holiday and entry operations do
/// not, as Pro's do; the same scenarios run against Pro's Postgres stores in the live-DB suite.
/// </summary>
public sealed class DialerSubResourceTenantScopeTests : IClassFixture<DialerTenantScopeFactory>
{
    internal const string CalendarsPath = "/api/admin/holiday-calendars";
    internal const string PoolsPath = "/api/admin/caller-id-pools";
    internal const string ChristmasDate = "2026-12-25";
    internal const string ChristmasName = "Christmas";
    internal const string NewYearsEveDate = "2026-12-31";
    internal const string PoolNumber = "+573001234567";
    internal const string ForeignNumber = "+573009999999";

    private const string Acme = DialerTenantScopeFactory.AcmeTenantId;
    private const string Globex = DialerTenantScopeFactory.GlobexTenantId;

    private readonly DialerTenantScopeFactory _factory;

    public DialerSubResourceTenantScopeTests(DialerTenantScopeFactory factory)
    {
        _factory = factory;
    }

    // ─── Holidays ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListHolidays_ShouldReturn404AndCarryNoHoliday_WhenTheCalendarBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var calendarId = await CreateCalendarAsync(acme);
        await AddHolidayAsync(acme, calendarId, ChristmasDate, ChristmasName);

        using var response = await globex.GetAsync($"{CalendarsPath}/{calendarId}/holidays");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "calendar {0} belongs to acme, so globex has no holidays of it to show", calendarId);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(ChristmasName);
    }

    [Fact]
    public async Task AddHoliday_ShouldReturn404AndLeaveTheCalendarUnchanged_WhenTheCalendarBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var calendarId = await CreateCalendarAsync(acme);
        await AddHolidayAsync(acme, calendarId, ChristmasDate, ChristmasName);

        using var sameDate = await globex.PostAsync($"{CalendarsPath}/{calendarId}/holidays",
            HolidayBody(ChristmasDate, "Renamed by globex"));
        using var newDate = await globex.PostAsync($"{CalendarsPath}/{calendarId}/holidays",
            HolidayBody(NewYearsEveDate, "Added by globex"));

        sameDate.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "calendar {0} belongs to acme, so globex cannot rename its holiday", calendarId);
        newDate.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "calendar {0} belongs to acme, so globex cannot add a holiday to it", calendarId);
        (await StoredHolidaysAsync(calendarId)).Should().Equal([(ChristmasDate, ChristmasName)],
            "calendar {0} holds exactly its own holiday, under its own name", calendarId);
    }

    [Fact]
    public async Task RemoveHoliday_ShouldReturn404AndKeepTheHoliday_WhenTheCalendarBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var calendarId = await CreateCalendarAsync(acme);
        var holidayId = await AddHolidayAsync(acme, calendarId, ChristmasDate, ChristmasName);

        using var response = await globex.DeleteAsync($"{CalendarsPath}/{calendarId}/holidays/{holidayId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "calendar {0} belongs to acme, so globex cannot remove its holiday {1}", calendarId, holidayId);
        (await StoredHolidaysAsync(calendarId)).Should().Equal([(ChristmasDate, ChristmasName)]);
    }

    [Fact]
    public async Task RemoveHoliday_ShouldKeepTheOtherCalendarsHoliday_WhenTheHolidayIsNotInTheCallersCalendar()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var acmeCalendarId = await CreateCalendarAsync(acme);
        var globexCalendarId = await CreateCalendarAsync(globex);
        var globexHolidayId = await AddHolidayAsync(globex, globexCalendarId, ChristmasDate, ChristmasName);

        using var response = await acme.DeleteAsync($"{CalendarsPath}/{acmeCalendarId}/holidays/{globexHolidayId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "calendar {0} is acme's own, and a holiday id outside it keeps today's answer", acmeCalendarId);
        (await StoredHolidaysAsync(globexCalendarId)).Should().Equal([(ChristmasDate, ChristmasName)],
            "holiday {0} is in globex's calendar {1}, not in acme's calendar {2}",
            globexHolidayId, globexCalendarId, acmeCalendarId);
    }

    [Fact]
    public async Task HolidayRoutes_ShouldAnswerAsBefore_WhenTheCalendarBelongsToTheTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var calendarId = await CreateCalendarAsync(acme);

        using var add = await acme.PostAsync($"{CalendarsPath}/{calendarId}/holidays", HolidayBody(ChristmasDate, ChristmasName));
        add.StatusCode.Should().Be(HttpStatusCode.Created);

        using var list = await acme.GetAsync($"{CalendarsPath}/{calendarId}/holidays");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var holidays = JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray();
        holidays.Select(n => n!["name"]!.GetValue<string>()).Should().Equal([ChristmasName]);
        var holidayId = holidays[0]!["id"]!.GetValue<long>();

        using var remove = await acme.DeleteAsync($"{CalendarsPath}/{calendarId}/holidays/{holidayId}");
        remove.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredHolidaysAsync(calendarId)).Should().BeEmpty();
    }

    // ─── Caller-ID entries ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListEntries_ShouldReturn404AndCarryNoEntry_WhenThePoolBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var poolId = await CreatePoolAsync(acme);
        await AddEntryAsync(acme, poolId, PoolNumber);

        using var response = await globex.GetAsync($"{PoolsPath}/{poolId}/entries");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "pool {0} belongs to acme, so globex has no entries of it to show", poolId);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(PoolNumber);
    }

    [Fact]
    public async Task AddEntry_ShouldReturn404AndLeaveThePoolUnchanged_WhenThePoolBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var poolId = await CreatePoolAsync(acme);
        await AddEntryAsync(acme, poolId, PoolNumber);

        using var response = await globex.PostAsync($"{PoolsPath}/{poolId}/entries", EntryBody(ForeignNumber));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "pool {0} belongs to acme, so globex cannot add an entry to it", poolId);
        (await StoredEntriesAsync(poolId)).Should().Equal([PoolNumber], "pool {0} holds exactly its own entry", poolId);
    }

    [Fact]
    public async Task RemoveEntry_ShouldReturn404AndKeepTheEntry_WhenThePoolBelongsToAnotherTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var poolId = await CreatePoolAsync(acme);
        var entryId = await AddEntryAsync(acme, poolId, PoolNumber);

        using var response = await globex.DeleteAsync($"{PoolsPath}/{poolId}/entries/{entryId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "pool {0} belongs to acme, so globex cannot remove its entry {1}", poolId, entryId);
        (await StoredEntriesAsync(poolId)).Should().Equal([PoolNumber]);
    }

    [Fact]
    public async Task RemoveEntry_ShouldKeepTheOtherPoolsEntry_WhenTheEntryIsNotInTheCallersPool()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        using var globex = _factory.CreateAdminClient(Globex);
        var acmePoolId = await CreatePoolAsync(acme);
        var globexPoolId = await CreatePoolAsync(globex);
        var globexEntryId = await AddEntryAsync(globex, globexPoolId, PoolNumber);

        using var response = await acme.DeleteAsync($"{PoolsPath}/{acmePoolId}/entries/{globexEntryId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "pool {0} is acme's own, and an entry id outside it keeps today's answer", acmePoolId);
        (await StoredEntriesAsync(globexPoolId)).Should().Equal([PoolNumber],
            "entry {0} is in globex's pool {1}, not in acme's pool {2}", globexEntryId, globexPoolId, acmePoolId);
    }

    [Fact]
    public async Task EntryRoutes_ShouldAnswerAsBefore_WhenThePoolBelongsToTheTenant()
    {
        using var acme = _factory.CreateAdminClient(Acme);
        var poolId = await CreatePoolAsync(acme);

        using var add = await acme.PostAsync($"{PoolsPath}/{poolId}/entries", EntryBody(PoolNumber));
        add.StatusCode.Should().Be(HttpStatusCode.Created);

        using var list = await acme.GetAsync($"{PoolsPath}/{poolId}/entries");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray();
        entries.Select(n => n!["phoneNumber"]!.GetValue<string>()).Should().Equal([PoolNumber]);
        var entryId = entries[0]!["id"]!.GetValue<long>();

        using var remove = await acme.DeleteAsync($"{PoolsPath}/{poolId}/entries/{entryId}");
        remove.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StoredEntriesAsync(poolId)).Should().BeEmpty();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>The calendar's holidays as (date, name), read from the in-memory store (its holiday reads are keyed by the calendar id only).</summary>
    private async Task<IReadOnlyList<(string Date, string Name)>> StoredHolidaysAsync(long calendarId)
    {
        var store = _factory.Services.GetRequiredService<HolidayCalendarStoreBase>();
        var holidays = await store.ListHolidaysAsync(calendarId, Acme, CancellationToken.None);
        return [.. holidays.Select(h => (h.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), h.Name))];
    }

    /// <summary>The pool's entry numbers, read from the in-memory store (its entry reads are keyed by the pool id only).</summary>
    private async Task<IReadOnlyList<string>> StoredEntriesAsync(long poolId)
    {
        var store = _factory.Services.GetRequiredService<CallerIdPoolStoreBase>();
        var entries = await store.ListEntriesAsync(poolId, Acme, CancellationToken.None);
        return [.. entries.Select(e => e.PhoneNumber)];
    }

    internal static async Task<long> CreateCalendarAsync(HttpClient client)
    {
        using var response = await client.PostAsync(CalendarsPath, JsonContent.Create(new { name = $"calendar-{Guid.NewGuid():N}" }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    internal static async Task<long> CreatePoolAsync(HttpClient client)
    {
        using var response = await client.PostAsync(PoolsPath, JsonContent.Create(new { name = $"pool-{Guid.NewGuid():N}" }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    /// <summary>
    /// Adds a holiday through the owner's own calendar and returns its id, read from the list: Pro's add does not
    /// return the generated id, so the 201 body carries none against Pro's stores.
    /// </summary>
    internal static async Task<long> AddHolidayAsync(HttpClient owner, long calendarId, string date, string name)
    {
        using (var add = await owner.PostAsync($"{CalendarsPath}/{calendarId}/holidays", HolidayBody(date, name)))
            add.StatusCode.Should().Be(HttpStatusCode.Created);
        using var list = await owner.GetAsync($"{CalendarsPath}/{calendarId}/holidays");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray()
            .Single(n => n!["date"]!.GetValue<string>() == date)!["id"]!.GetValue<long>();
    }

    /// <summary>Adds an entry through the owner's own pool and returns its id, read from the list (see <see cref="AddHolidayAsync"/>).</summary>
    internal static async Task<long> AddEntryAsync(HttpClient owner, long poolId, string phoneNumber)
    {
        using (var add = await owner.PostAsync($"{PoolsPath}/{poolId}/entries", EntryBody(phoneNumber)))
            add.StatusCode.Should().Be(HttpStatusCode.Created);
        using var list = await owner.GetAsync($"{PoolsPath}/{poolId}/entries");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray()
            .Single(n => n!["phoneNumber"]!.GetValue<string>() == phoneNumber)!["id"]!.GetValue<long>();
    }

    internal static JsonContent HolidayBody(string date, string name) => JsonContent.Create(new { date, name });

    internal static JsonContent EntryBody(string phoneNumber) => JsonContent.Create(new { phoneNumber, isActive = true });
}
