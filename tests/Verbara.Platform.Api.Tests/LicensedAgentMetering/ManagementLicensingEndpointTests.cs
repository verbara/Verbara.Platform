using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.Tests.Auth;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.LicensedAgentMetering;

/// <summary>
/// The account-status host with a report reader whose peaks a test can replace (the in-memory host runs no
/// daily close, so it has no closed day of its own). The export always reads the real in-memory ledger.
/// </summary>
public sealed class LicensedAgentReportApiFactory : AccountStatusApiFactory
{
    /// <summary>When set, the peaks report reads these rows instead of the in-memory ledger's (none).</summary>
    public IReadOnlyList<LicenseAgentDaily>? PeaksRows { get; set; }

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddSingleton<ILicensedAgentReportReader>(sp => new Reader(
            this,
            new InMemoryLicensedAgentReportReader(
                sp.GetRequiredService<InMemoryLicenseAgentLedger>(), sp.GetRequiredService<ITenantStore>())));

    private sealed class Reader(LicensedAgentReportApiFactory factory, ILicensedAgentReportReader inner) : ILicensedAgentReportReader
    {
        public async Task<LicensedAgentPeaksData> ReadPeaksAsync(DateOnly firstDay, DateOnly lastDay, string fallbackDayZone, CancellationToken ct)
        {
            var data = await inner.ReadPeaksAsync(firstDay, lastDay, fallbackDayZone, ct);
            return factory.PeaksRows is { } rows ? data with { Daily = rows } : data;
        }

        public Task<LicensedAgentExportData> ReadExportAsync(DateOnly firstDay, DateOnly lastDay, string fallbackDayZone, CancellationToken ct) =>
            inner.ReadExportAsync(firstDay, lastDay, fallbackDayZone, ct);
    }
}

/// <summary>
/// licensed-agent-metering slice 3 (tasks.md 7.2/7.3; licensed-agent-reporting) over HTTP:
/// <c>GET /management/licensing/agents</c> and <c>/agents/export</c> are PlatformAdminOnly, enforce the
/// 460-day range rule, compute <c>overBand</c> server-side without ever blocking anything, report no peak day
/// when no day is closed, and export rows an independent verifier can re-hash from the JSON alone.
/// </summary>
public sealed class ManagementLicensingEndpointTests : IClassFixture<LicensedAgentReportApiFactory>
{
    private const string Peaks = "/api/v1/management/licensing/agents";
    private const string Export = "/api/v1/management/licensing/agents/export";
    private static readonly TenantId Customer = new(AccountStatusApiFactory.CustomerTenantId);

    private readonly LicensedAgentReportApiFactory _factory;

    public ManagementLicensingEndpointTests(LicensedAgentReportApiFactory factory)
    {
        _factory = factory;
        _factory.PeaksRows = null;
        License(licenseId: null, maxAgents: 0);
    }

    [Theory]
    [InlineData(Peaks)]
    [InlineData(Export)]
    public async Task Reports_ShouldReturn403_ForATenantAdmin(string path)
    {
        using var client = Bearer(AccountStatusApiFactory.CustomerAdminUserId, AccountStatusApiFactory.CustomerTenantId);

        var response = await client.GetAsync($"{path}?from=2026-09-01&to=2026-09-30");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(Peaks)]
    [InlineData(Export)]
    public async Task Reports_ShouldReturn401_WithoutCredentials(string path)
    {
        using var client = _factory.CreateClient();

        (await client.GetAsync($"{path}?from=2026-09-01&to=2026-09-30")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(Peaks, "from=2026-09-30&to=2026-09-01")]
    [InlineData(Peaks, "from=2026-09-01")]
    [InlineData(Peaks, "from=2026-9-1&to=2026-09-30")]
    [InlineData(Peaks, "from=2024-01-01&to=2025-04-05")]   // 461 days
    [InlineData(Export, "from=2024-01-01&to=2025-04-05")]
    [InlineData(Export, "to=2026-09-30")]
    public async Task Reports_ShouldReturn400_ForABadRange(string path, string query)
    {
        using var client = PlatformAdmin();

        var response = await client.GetAsync($"{path}?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("460");
    }

    [Theory]
    [InlineData(Peaks)]
    [InlineData(Export)]
    public async Task Reports_ShouldAccept15CalendarMonths_InOneCall(string path)
    {
        using var client = PlatformAdmin();

        var response = await client.GetAsync($"{path}?from=2025-07-01&to=2026-09-30");   // 457 days

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["from"]!.GetValue<string>().Should().Be("2025-07-01");
        body["to"]!.GetValue<string>().Should().Be("2026-09-30");
    }

    [Fact]
    public async Task Peaks_ShouldHaveNoPeakDay_WhenNoDayOfTheRangeIsClosed()
    {
        using var client = PlatformAdmin();

        var body = await GetJsonAsync(client, $"{Peaks}?from=2026-10-01&to=2026-10-31");

        body["deployment"]!["peakDay"].Should().BeNull();
        body["deployment"]!.AsObject().ContainsKey("peakDay").Should().BeTrue();
        body["deployment"]!["peakLicensedAgents"]!.GetValue<int>().Should().Be(0);
        body["deployment"]!["overBand"]!.GetValue<bool>().Should().BeFalse();
        body["days"]!.AsArray().Should().BeEmpty();
        body["dayZone"]!.GetValue<string>().Should().Be("UTC");
        body["license"]!["maxAgentsAdvisory"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task Peaks_ShouldNeverBeOverBand_WhenMaxAgentsIsNotDeclared(int? maxAgents)
    {
        License(licenseId: maxAgents is null ? null : "lic-undeclared", maxAgents: maxAgents ?? 0);
        _factory.PeaksRows = [Row(null, new DateOnly(2026, 9, 10), 120)];
        using var client = PlatformAdmin();

        var body = await GetJsonAsync(client, $"{Peaks}?from=2026-09-01&to=2026-09-30");

        body["deployment"]!["peakLicensedAgents"]!.GetValue<int>().Should().Be(120);
        body["deployment"]!["overBand"]!.GetValue<bool>().Should().BeFalse();
        body["license"]!["maxAgentsAdvisory"]!.GetValue<bool>().Should().BeTrue();
        if (maxAgents is null)
            body["license"]!["maxAgents"].Should().BeNull();
        else
            body["license"]!["maxAgents"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Peaks_ShouldWarnAboveTheBand_AndBlockNothing()
    {
        License(licenseId: "lic-band-50", maxAgents: 50);
        var day = new DateOnly(2026, 9, 17);
        _factory.PeaksRows = [Row(null, day, 63), Row(AccountStatusApiFactory.CustomerTenantId, day, 63)];
        using var client = PlatformAdmin();

        var body = await GetJsonAsync(client, $"{Peaks}?from=2026-09-01&to=2026-09-30");

        body["deployment"]!["overBand"]!.GetValue<bool>().Should().BeTrue();
        body["deployment"]!["peakDay"]!.GetValue<string>().Should().Be("2026-09-17");
        body["days"]![0]!["tenants"]![0]!["tenantName"]!.GetValue<string>().Should().Be(AccountStatusApiFactory.CustomerTenantId);
        body["license"]!["licenseId"]!.GetValue<string>().Should().Be("lic-band-50");

        // Nothing is blocked on it: a tenant admin still creates an agent.
        var user = SeedUser();
        using var admin = Bearer(AccountStatusApiFactory.CustomerAdminUserId, AccountStatusApiFactory.CustomerTenantId);
        var created = await admin.PostAsJsonAsync("/api/v1/admin/agents", new { userId = user.UserId.Value, displayName = "Over band" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Export_ShouldVerifyOfflineFromItsJson_AndEndAtEveryChainHead_WhenTheRangeRunsToToday()
    {
        License(licenseId: "lic-export-1", maxAgents: 10);
        using var admin = Bearer(AccountStatusApiFactory.CustomerAdminUserId, AccountStatusApiFactory.CustomerTenantId);
        var first = SeedUser();
        var second = SeedUser();
        (await admin.PostAsJsonAsync("/api/v1/admin/agents", new { userId = first.UserId.Value, displayName = "Exported 1" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        License(licenseId: "lic-export-2", maxAgents: 10);   // a renewal: the next row re-anchors the chain
        (await admin.PostAsJsonAsync("/api/v1/admin/agents", new { userId = second.UserId.Value, displayName = "Exported 2" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await admin.PutAsJsonAsync($"/api/v1/admin/users/{first.UserId.Value}", new { status = "Suspended" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rowsBefore = Ledger().Events(Customer.Value).Count;
        using var client = PlatformAdmin();
        var body = await GetJsonAsync(client, $"{Export}?from={today.AddMonths(-14):yyyy-MM-01}&to={today:yyyy-MM-dd}");

        body["hashScheme"]!.GetValue<string>().Should().Be("lac1");
        var events = body["events"]!.AsArray();
        events.Select(e => e!["kind"]!.GetValue<string>()).Should().Contain(
            [LicenseAgentEventKinds.ChainAnchored, LicenseAgentEventKinds.AgentCreated, LicenseAgentEventKinds.ChainReanchored,
             LicenseAgentEventKinds.UserStatusChanged]);
        var heads = body["chainHeads"]!.AsArray();
        heads.Should().NotBeEmpty();
        foreach (var head in heads)
        {
            var tenantId = head!["tenantId"]?.GetValue<string>();
            var chain = events.Where(e => e!["tenantId"]?.GetValue<string>() == tenantId).ToList();
            chain.Should().NotBeEmpty($"chain {tenantId ?? "deployment"} has rows in a range that runs to today");
            IndependentVerifier.VerifyChain(tenantId, chain).Should().BeNull($"chain {tenantId ?? "deployment"} verifies");
            chain[^1]!["rowHash"]!.GetValue<string>().Should().Be(head["headHash"]!.GetValue<string>());
            chain[^1]!["sequence"]!.GetValue<long>().Should().Be(head["headSequence"]!.GetValue<long>());
        }

        Ledger().Events(Customer.Value).Count.Should().Be(rowsBefore, "the export never writes a ledger row");
    }

    [Fact]
    public async Task Export_ShouldBeRefutedByTheVerifier_WhenARowIsTampered()
    {
        License(licenseId: "lic-tamper", maxAgents: 10);
        using var admin = Bearer(AccountStatusApiFactory.CustomerAdminUserId, AccountStatusApiFactory.CustomerTenantId);
        (await admin.PostAsJsonAsync("/api/v1/admin/agents", new { userId = SeedUser().UserId.Value, displayName = "T" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var client = PlatformAdmin();
        var body = await GetJsonAsync(client, $"{Export}?from={today:yyyy-MM-01}&to={today:yyyy-MM-dd}");
        var chain = body["events"]!.AsArray().Where(e => e!["tenantId"]?.GetValue<string>() == Customer.Value).ToList();

        var victim = chain[^1]!;
        var sequence = victim["sequence"]!.GetValue<long>();
        victim["counted"] = !victim["counted"]!.GetValue<bool>();

        IndependentVerifier.VerifyChain(Customer.Value, chain).Should().Be(sequence, "the verifier names the altered row");
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private void License(string? licenseId, int maxAgents)
    {
        var status = _factory.Services.GetRequiredService<ILicenseStatus>();
        status.LicenseId.Returns(licenseId);
        status.Licensee.Returns(licenseId is null ? null : "Example Contact Ltd");
        status.Tier.Returns(licenseId is null ? LicenseTier.None : LicenseTier.SelfHostBusiness);
        status.MaxAgents.Returns(maxAgents);
    }

    private InMemoryLicenseAgentLedger Ledger() => _factory.Services.GetRequiredService<InMemoryLicenseAgentLedger>();

    private static LicenseAgentDaily Row(string? tenantId, DateOnly day, int agents) => new()
    {
        TenantId = tenantId,
        Sequence = 1,
        Day = day,
        Revision = 0,
        LicensedAgents = agents,
        ClosedAt = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1),
        ClosedThroughSequence = 0,
        LicenseId = null,
        PrevHash = "lac1:prev",
        RowHash = "lac1:row",
    };

    private User SeedUser() => _factory.SaveUser(
        $"lam-report-{Guid.NewGuid():N}", AccountStatusApiFactory.CustomerTenantId, UserRole.Agent, UserStatus.Active);

    private HttpClient PlatformAdmin() => Bearer(AccountStatusApiFactory.PlatformAdminUserId, AccountStatusApiFactory.PlatformTenantId);

    private HttpClient Bearer(string userId, string tenantId) =>
        _factory.CreateBearerClient(_factory.MintAccessToken(_factory.GetUser(userId, tenantId)!));

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!;
    }
}

/// <summary>
/// An offline verifier written from the published canonical form only (docs/operations/licensed-agent-ledger.md),
/// with no Platform code: it re-renders each row from its JSON fields, re-hashes it and links it to its
/// predecessor. Returns the sequence of the first row that fails, or null.
/// </summary>
internal static class IndependentVerifier
{
    public static long? VerifyChain(string? tenantId, IReadOnlyList<JsonNode?> rows)
    {
        string? previous = null;
        long? previousSequence = null;
        foreach (var row in rows.OrderBy(r => r!["sequence"]!.GetValue<long>()))
        {
            var sequence = row!["sequence"]!.GetValue<long>();
            var prevHash = row["prevHash"]!.GetValue<string>();
            var expectedPrev = previous
                ?? (sequence == 1 ? Hash($"genesis|{tenantId ?? "deployment"}|{Field(row, "licenseId")}") : prevHash);
            if ((previousSequence is { } p && sequence != p + 1)
                || prevHash != expectedPrev
                || row["rowHash"]!.GetValue<string>() != Hash(Canonical(row)))
                return sequence;
            previous = row["rowHash"]!.GetValue<string>();
            previousSequence = sequence;
        }

        return null;
    }

    private static readonly string[] EventFields =
    [
        "prevHash", "tenantId", "sequence", "eventId", "kind", "occurredAt", "agentId", "userId", "actorUserId",
        "conversationId", "userStatus", "counted", "licenseId",
    ];

    private static readonly string[] DailyFields =
    [
        "prevHash", "tenantId", "sequence", "day", "revision", "licensedAgents", "closedAt", "closedThroughSequence", "licenseId",
    ];

    private static string Canonical(JsonNode row) =>
        string.Join('|', (row["kind"] is not null ? EventFields : DailyFields).Select(f => Field(row, f)));

    // A null renders as the empty string, a boolean in lowercase, a number in the invariant culture; strings
    // (instants already in the canonical UTC form) as they are on the wire.
    private static string Field(JsonNode row, string name) => row[name] switch
    {
        null => "",
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.True => "true",
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.False => "false",
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number => v.GetValue<long>().ToString(CultureInfo.InvariantCulture),
        JsonValue v => v.GetValue<string>(),
        _ => throw new InvalidOperationException(name),
    };

    private static string Hash(string canonical) =>
        "lac1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
