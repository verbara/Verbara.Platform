using System.Net;
using Npgsql;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-references-stay-in-their-tenant, against Pro's Postgres stores: the real Api <c>Program</c> with the dialer
/// registered over a Testcontainers Postgres (<see cref="DialerTenantScopeFactory"/> in live-DB mode keeps Pro's trunk
/// store, as <c>Program.cs</c> decorates it, and Pro's outbound-route store). The scenarios of
/// <see cref="OutboundRouteTenantScopeScenarios"/> run here too, over real rows: a route names only trunks of the
/// caller's tenant, and a refused request writes nothing, which the trunk delete below also checks by SQL.
/// </summary>
public sealed class DialerOutboundRouteTenantScopeLiveDbTests
    : OutboundRouteTenantScopeScenarios, IClassFixture<DialerTenantScopeLiveDbFixture>
{
    private readonly DialerTenantScopeLiveDbFixture _fixture;

    public DialerOutboundRouteTenantScopeLiveDbTests(DialerTenantScopeLiveDbFixture fixture)
    {
        _fixture = fixture;
    }

    /// <inheritdoc />
    protected override DialerTenantScopeFactory Factory => _fixture.Factory;

    [Fact]
    public async Task DeleteTrunk_ShouldReturn204AndRemoveTheTrunk_WhenARouteCreateNamingItWasRefused()
    {
        using var acme = Factory.CreateAdminClient(Acme);
        using var globex = Factory.CreateAdminClient(Globex);
        var acmeTrunk = await CreateTrunkAsync(acme);
        using var refused = await globex.PostAsync(RoutesPath, RouteBody(NewPattern(), acmeTrunk, overflowTrunkId: null));
        var refusedStatus = (int)refused.StatusCode;

        using var response = await acme.DeleteAsync($"{TrunksPath}/{acmeTrunk}");

        ((int)response.StatusCode).Should().Be(204,
            "trunk {0} is acme's, and the route create naming it was refused (it answered {1})",
            acmeTrunk, refusedStatus);
        (await CountAsync("SELECT COUNT(*) FROM trunks WHERE id = @Id", acmeTrunk))
            .Should().Be(0, "acme's trunk {0} is gone", acmeTrunk);
        (await CountAsync("SELECT COUNT(*) FROM outbound_routes WHERE trunk_id = @Id", acmeTrunk))
            .Should().Be(0, "no route of any tenant names acme's deleted trunk");
    }

    private async Task<long> CountAsync(string sql, long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
