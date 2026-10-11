using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// partner-channel-pilot-fixes 1.7 (partner-channel-tenancy, design D6): migration
/// <c>021_HostTenantPlanEnterprise.sql</c> against a real Postgres — a host set up before this change, with no
/// plan, gains <c>Plan = Enterprise</c> and keeps its other metadata; a host with an explicit plan keeps it; a
/// second run changes nothing; no other tenant is touched.
/// </summary>
public sealed class HostTenantPlanMigrationTests : IClassFixture<AgentIdentityFixture>
{
    private const string Migration021 = "021_HostTenantPlanEnterprise.sql";
    private readonly AgentIdentityFixture _fixture;

    public HostTenantPlanMigrationTests(AgentIdentityFixture fixture) => _fixture = fixture;

    private static async Task SetMetadataAsync(NpgsqlDataSource ds, string tenantId, string? json)
    {
        await using var cmd = ds.CreateCommand("UPDATE tenants SET metadata = @Metadata::jsonb WHERE tenant_id = @TenantId");
        cmd.Parameters.Add(new NpgsqlParameter("Metadata", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)json ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> MetadataAsync(NpgsqlDataSource ds, string tenantId)
    {
        await using var cmd = ds.CreateCommand("SELECT metadata::text FROM tenants WHERE tenant_id = @TenantId");
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task<string?> PlanAsync(NpgsqlDataSource ds, string tenantId)
    {
        await using var cmd = ds.CreateCommand("SELECT metadata ->> 'Plan' FROM tenants WHERE tenant_id = @TenantId");
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
        return await cmd.ExecuteScalarAsync() as string;
    }

    [Fact]
    public async Task HostTenantPlan_ShouldBackfillEnterprise_WhenHostHasNoPlan()
    {
        var ds = await _fixture.CreateDatabaseAsync(before018: false);
        await AgentIdentityFixture.InsertTenantAsync(ds, "platform", type: 0, parent: null);
        await AgentIdentityFixture.InsertTenantAsync(ds, "dc1", type: 2, parent: "platform");
        await AgentIdentityFixture.InsertTenantAsync(ds, "legacy", type: 2, parent: "platform");
        await SetMetadataAsync(ds, "legacy", """{"SupportRegion":"latam"}""");

        await AgentIdentityFixture.ApplyMigrationAsync(ds, Migration021);

        (await PlanAsync(ds, "platform")).Should().Be("Enterprise");
        (await MetadataAsync(ds, "dc1")).Should().BeNull("only the Platform tenant is backfilled");
        (await PlanAsync(ds, "legacy")).Should().BeNull();
    }

    [Fact]
    public async Task HostTenantPlan_ShouldKeepOtherMetadataKeys_WhenHostHasMetadataButNoPlan()
    {
        var ds = await _fixture.CreateDatabaseAsync(before018: false);
        await AgentIdentityFixture.InsertTenantAsync(ds, "platform", type: 0, parent: null);
        await SetMetadataAsync(ds, "platform", """{"RateLimitTier":"Enterprise"}""");

        await AgentIdentityFixture.ApplyMigrationAsync(ds, Migration021);

        (await PlanAsync(ds, "platform")).Should().Be("Enterprise");
        (await MetadataAsync(ds, "platform")).Should().Contain("\"RateLimitTier\": \"Enterprise\"", "the key is merged, never replaced");
    }

    [Fact]
    public async Task HostTenantPlan_ShouldKeepExplicitPlan_WhenHostAlreadyHoldsOne()
    {
        var ds = await _fixture.CreateDatabaseAsync(before018: false);
        await AgentIdentityFixture.InsertTenantAsync(ds, "platform", type: 0, parent: null);
        await SetMetadataAsync(ds, "platform", """{"Plan":"Pro"}""");

        await AgentIdentityFixture.ApplyMigrationAsync(ds, Migration021);

        (await PlanAsync(ds, "platform")).Should().Be("Pro", "an explicit plan is never overridden");
    }

    [Fact]
    public async Task HostTenantPlan_ShouldChangeNothing_WhenAppliedTwice()
    {
        var ds = await _fixture.CreateDatabaseAsync(before018: false);
        await AgentIdentityFixture.InsertTenantAsync(ds, "platform", type: 0, parent: null);
        await AgentIdentityFixture.ApplyMigrationAsync(ds, Migration021);
        var first = await MetadataAsync(ds, "platform");
        var stamp = await AgentIdentityFixture.ScalarAsync(ds,
            "SELECT (extract(epoch FROM updated_at) * 1000000)::bigint FROM tenants WHERE tenant_id = 'platform'");

        await AgentIdentityFixture.ApplyMigrationAsync(ds, Migration021);

        (await MetadataAsync(ds, "platform")).Should().Be(first);
        (await AgentIdentityFixture.ScalarAsync(ds,
            "SELECT (extract(epoch FROM updated_at) * 1000000)::bigint FROM tenants WHERE tenant_id = 'platform'"))
            .Should().Be(stamp, "a second run matches no row");
    }
}
