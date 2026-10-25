using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Storage.Postgres.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-honours-dnc-callbacks-and-agent-dispositions (design D1, D13): the one-shot campaign repairs against Pro's real
/// dialer schema (applied by <c>UsePostgresDialerStorage</c>). The repairs run once per database, so every test gets a
/// database of its own on one Testcontainers Postgres.
/// </summary>
public sealed class DialerDataRepairsLiveDbTests : IAsyncLifetime
{
    private static readonly DateTimeOffset BeforeCutoff = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterCutoff = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);

    private IContainer? _postgres;
    private readonly List<NpgsqlDataSource> _dataSources = [];
    private readonly IAuditService _audit = Substitute.For<IAuditService>();

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new ContainerBuilder("postgres:16-alpine")
            .WithName($"verbara-dialer-repairs-pg-{Guid.NewGuid():N}")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _postgres.StartAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        foreach (var dataSource in _dataSources)
            await dataSource.DisposeAsync();
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    private string ConnectionString(string database) =>
        $"Host={_postgres!.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
        $"Database={database};Username=postgres;Password=postgres";

    /// <summary>A fresh database, with Pro's dialer schema when <paramref name="withDialerSchema"/>.</summary>
    private async Task<NpgsqlDataSource> NewDatabaseAsync(bool withDialerSchema = true)
    {
        var name = $"repairs_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(ConnectionString("postgres")))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var dataSource = NpgsqlDataSource.Create(ConnectionString(name));
        _dataSources.Add(dataSource);
        if (withDialerSchema)
            new ServiceCollection().UsePostgresDialerStorage(dataSource); // runs the dialer migrations
        return dataSource;
    }

    private DialerDataRepairs Repairs(NpgsqlDataSource dataSource) =>
        new(dataSource, _audit, NullLogger<DialerDataRepairs>.Instance);

    private static async Task<long> InsertCampaignAsync(
        NpgsqlDataSource dataSource, string tenantId, DateTimeOffset createdAt, bool checkDnc, int mode = 2, double rate = 0.03)
    {
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO campaigns (tenant_id, name, created_at, check_global_dnc, mode, target_abandon_rate) " +
            "VALUES (@T, 'repair', @C, @D, @M, @R) RETURNING id");
        cmd.Parameters.AddWithValue("T", tenantId);
        cmd.Parameters.AddWithValue("C", createdAt.UtcDateTime);
        cmd.Parameters.AddWithValue("D", checkDnc);
        cmd.Parameters.AddWithValue("M", mode);
        cmd.Parameters.AddWithValue("R", rate);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<(bool CheckDnc, double Rate)> CampaignAsync(NpgsqlDataSource dataSource, long id)
    {
        await using var cmd = dataSource.CreateCommand("SELECT check_global_dnc, target_abandon_rate FROM campaigns WHERE id = @Id");
        cmd.Parameters.AddWithValue("Id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetBoolean(0), reader.GetDouble(1));
    }

    private Task AuditedAsync(int times, string action, string tenantId, long campaignId,
        Func<IReadOnlyDictionary<string, string>, bool>? metadata = null) =>
        _audit.Received(times).RecordAsync(
            Arg.Is<TenantId>(t => t.Value == tenantId), "config", action, Arg.Any<string>(), Arg.Any<string>(), "system",
            campaignId.ToString(System.Globalization.CultureInfo.InvariantCulture), "campaign",
            Arg.Any<Guid?>(), Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m => m != null && (metadata == null || metadata(m))),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());

    // ─── B.2: the DNC repair (#472) ──────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ShouldTurnTheDncCheckOnAndAudit_WhenACampaignCreatedBeforeTheCutoffHasItOff()
    {
        var db = await NewDatabaseAsync();
        var id = await InsertCampaignAsync(db, "acme", BeforeCutoff, checkDnc: false);

        await Repairs(db).RunAsync(CancellationToken.None);

        (await CampaignAsync(db, id)).CheckDnc.Should().BeTrue();
        await AuditedAsync(1, DialerDataRepairs.DncRepairStep, "acme", id);
    }

    [Fact]
    public async Task RunAsync_ShouldLeaveTheDncCheck_WhenTheCampaignWasCreatedAfterTheCutoff()
    {
        var db = await NewDatabaseAsync();
        var id = await InsertCampaignAsync(db, "acme", AfterCutoff, checkDnc: false);

        await Repairs(db).RunAsync(CancellationToken.None);

        (await CampaignAsync(db, id)).CheckDnc.Should().BeFalse();
        await AuditedAsync(0, DialerDataRepairs.DncRepairStep, "acme", id);
    }

    [Fact]
    public async Task RunAsync_ShouldChangeNothing_WhenItRunsAgainAfterAnAdministratorTurnedTheCheckOff()
    {
        var db = await NewDatabaseAsync();
        var id = await InsertCampaignAsync(db, "acme", BeforeCutoff, checkDnc: false);
        await Repairs(db).RunAsync(CancellationToken.None);
        await using (var off = db.CreateCommand("UPDATE campaigns SET check_global_dnc = FALSE WHERE id = @Id"))
        {
            off.Parameters.AddWithValue("Id", id);
            await off.ExecuteNonQueryAsync();
        }

        await Repairs(db).RunAsync(CancellationToken.None);

        (await CampaignAsync(db, id)).CheckDnc.Should().BeFalse("the repair runs once per database");
        await AuditedAsync(1, DialerDataRepairs.DncRepairStep, "acme", id);
    }

    [Fact]
    public async Task RunAsync_ShouldLeaveTheDatabaseUnchanged_WhenItHasNoDialerSchema()
    {
        var db = await NewDatabaseAsync(withDialerSchema: false);

        await Repairs(db).RunAsync(CancellationToken.None);

        await using var cmd = db.CreateCommand("SELECT to_regclass('platform_one_shot_repairs') IS NULL");
        (await cmd.ExecuteScalarAsync()).Should().Be(true, "no ledger is written without the dialer schema, so the repairs run once it exists");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    // ─── B.8: the abandon-rate repair (owner Q5) ─────────────────────────────

    [Fact]
    public async Task RunAsync_ShouldRepairTheRateAndAuditBothValues_WhenAPredictiveCampaignStoredATargetWait()
    {
        var db = await NewDatabaseAsync();
        var id = await InsertCampaignAsync(db, "acme", AfterCutoff, checkDnc: true, mode: 4, rate: 15);

        await Repairs(db).RunAsync(CancellationToken.None);

        (await CampaignAsync(db, id)).Rate.Should().Be(0.03);
        await AuditedAsync(1, DialerDataRepairs.AbandonRateRepairStep, "acme", id,
            m => m["oldTargetAbandonRate"] == "15" && m["newTargetAbandonRate"] == "0.03");
    }

    [Fact]
    public async Task RunAsync_ShouldKeepTheRate_WhenAPredictiveCampaignHasAValidRate()
    {
        var db = await NewDatabaseAsync();
        var id = await InsertCampaignAsync(db, "acme", AfterCutoff, checkDnc: true, mode: 4, rate: 0.05);

        await Repairs(db).RunAsync(CancellationToken.None);

        (await CampaignAsync(db, id)).Rate.Should().Be(0.05);
        await AuditedAsync(0, DialerDataRepairs.AbandonRateRepairStep, "acme", id);
    }

    [Fact]
    public async Task RunAsync_ShouldKeepTheRate_WhenTheCampaignIsNotPredictive()
    {
        var db = await NewDatabaseAsync();
        var id = await InsertCampaignAsync(db, "acme", AfterCutoff, checkDnc: true, mode: 2, rate: 15);

        await Repairs(db).RunAsync(CancellationToken.None);

        (await CampaignAsync(db, id)).Rate.Should().Be(15);
        await AuditedAsync(0, DialerDataRepairs.AbandonRateRepairStep, "acme", id);
    }

    [Fact]
    public async Task RunAsync_ShouldChangeNoRate_WhenTheAbandonRateRepairAlreadyRan()
    {
        var db = await NewDatabaseAsync();
        await Repairs(db).RunAsync(CancellationToken.None);
        var id = await InsertCampaignAsync(db, "acme", AfterCutoff, checkDnc: true, mode: 4, rate: 15);

        await Repairs(db).RunAsync(CancellationToken.None);

        (await CampaignAsync(db, id)).Rate.Should().Be(15, "the repair is one-shot; D14's 400 keeps new rows valid");
        await AuditedAsync(0, DialerDataRepairs.AbandonRateRepairStep, "acme", id);
    }

    [Fact]
    public async Task RunAsync_ShouldRepairOnce_WhenTwoReplicasRunItTogether()
    {
        var db = await NewDatabaseAsync();
        var id = await InsertCampaignAsync(db, "acme", BeforeCutoff, checkDnc: false, mode: 4, rate: 15);

        await Task.WhenAll(
            Task.Run(() => Repairs(db).RunAsync(CancellationToken.None)),
            Task.Run(() => Repairs(db).RunAsync(CancellationToken.None)));

        await AuditedAsync(1, DialerDataRepairs.DncRepairStep, "acme", id);
        await AuditedAsync(1, DialerDataRepairs.AbandonRateRepairStep, "acme", id);
    }
}
