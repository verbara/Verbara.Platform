using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// supervisor-silent-listen 2.8 (e), design D14 against a real Postgres: provisioning a supervisor's listen endpoint
/// writes no licensed-agent ledger row, and the daily close counts the same licensed agents as without it. (A listen
/// itself writes only audit entries; it touches no table of this database.)
/// </summary>
public sealed class SupervisorListenLicensingTests : IClassFixture<AgentIdentityFixture>
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly AgentIdentityFixture _fixture;

    public SupervisorListenLicensingTests(AgentIdentityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ListenEndpoint_ShouldWriteNoLedgerRow_AndNotChangeTheDailyLicensedAgents()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, Day1.AddHours(9));
        await h.TenantAsync("c1", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("c1", "a1");
        await h.UserAsync("c1", "sup1");
        await h.Close.PrepareAsync("UTC", CancellationToken.None);
        var eventsBefore = await h.CountAsync("SELECT count(*) FROM license_agent_events");

        var saved = await new PostgresSupervisorListenProfileStore(h.Ds).SaveAsync(new SupervisorListenProfile
        {
            TenantId = new TenantId("c1"), UserId = EntityId.From("sup1"), SipExtension = "sl0a1b2c3d", SipPassword = "pw",
            PhoneNumber = "+573001234567", CreatedAt = Day1.AddHours(10), UpdatedAt = Day1.AddHours(10),
        }, CancellationToken.None);
        h.Clock.UtcNow = Day1.AddDays(1).AddMinutes(5);
        await h.Close.CloseAsync(TimeZoneInfo.Utc, h.Clock.UtcNow, CancellationToken.None);

        saved.Should().BeTrue();
        (await h.CountAsync("SELECT count(*) FROM license_agent_events")).Should().Be(eventsBefore, "an endpoint is not an agents row");
        (await h.CountAsync("SELECT count(*) FROM agents WHERE tenant_id = 'c1'")).Should().Be(1);
        (await h.DailyAsync("c1")).Single().LicensedAgents.Should().Be(1, "only a1 is a licensed agent");
    }
}
