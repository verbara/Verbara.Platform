using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using static Verbara.Platform.Api.Tests.Dialer.DialerSubResourceTenantScopeTests;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-sub-resources-stay-in-their-tenant, against Pro's Postgres stores: the real Api <c>Program</c> with the
/// dialer registered over a Testcontainers Postgres (<see cref="DialerTenantScopeFactory"/> in live-DB mode keeps
/// Pro's holiday-calendar and caller-ID-pool stores). The holiday routes of a calendar and the entry routes of a pool
/// answer 404 for a calendar or pool of another tenant and change nothing; a remove through the caller's own calendar
/// or pool removes nothing of another; and the tenant's own calendar and pool work. Holidays and entries carry no
/// tenant of their own (ownership is the parent's), so there is no mismatched-row case to seed.
/// </summary>
public sealed class DialerSubResourceTenantScopeLiveDbTests : IClassFixture<DialerTenantScopeLiveDbFixture>
{
    private const string Acme = DialerTenantScopeFactory.AcmeTenantId;
    private const string Globex = DialerTenantScopeFactory.GlobexTenantId;

    private readonly DialerTenantScopeLiveDbFixture _fixture;

    public DialerSubResourceTenantScopeLiveDbTests(DialerTenantScopeLiveDbFixture fixture)
    {
        _fixture = fixture;
    }

    private DialerTenantScopeFactory Factory => _fixture.Factory;

    // ─── Holidays ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListHolidays_ShouldReturn404AndCarryNoHoliday_WhenTheCalendarBelongsToAnotherTenant()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
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
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
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
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
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
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
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
        using var acme = Factory.CreateAdminClient(Acme);
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
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
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
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
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
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
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
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
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
        using var acme = Factory.CreateAdminClient(Acme);
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

    /// <summary>The calendar's holidays as (date, name), read by SQL.</summary>
    private async Task<IReadOnlyList<(string Date, string Name)>> StoredHolidaysAsync(long calendarId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT date, name FROM holidays WHERE holiday_calendar_id = @Id ORDER BY date", conn);
        cmd.Parameters.AddWithValue("Id", calendarId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var holidays = new List<(string, string)>();
        while (await reader.ReadAsync())
        {
            var date = reader.GetFieldValue<DateOnly>(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            holidays.Add((date, reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }
        return holidays;
    }

    /// <summary>The pool's entry numbers, read by SQL.</summary>
    private async Task<IReadOnlyList<string>> StoredEntriesAsync(long poolId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT phone_number FROM caller_id_entries WHERE pool_id = @Id ORDER BY id", conn);
        cmd.Parameters.AddWithValue("Id", poolId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var numbers = new List<string>();
        while (await reader.ReadAsync())
            numbers.Add(reader.GetString(0));
        return numbers;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}
