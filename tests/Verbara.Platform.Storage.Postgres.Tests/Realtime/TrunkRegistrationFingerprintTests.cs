using Npgsql;
using Verbara.Platform.Storage.Postgres.Realtime;
using Verbara.Platform.Storage.Postgres.Tests.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Realtime;

/// <summary>
/// #385 — <see cref="TrunkRegistrationFingerprint"/> against a real Postgres. <c>ps_registrations</c> belongs to
/// the Asterisk Realtime schema (Pro migration), not to Platform's migrations, so the test creates the columns
/// the fingerprint reads; each test starts from an empty table.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TrunkRegistrationFingerprintTests : IClassFixture<MigrationsFixture>, IAsyncLifetime
{
    private readonly MigrationsFixture _fixture;

    public TrunkRegistrationFingerprintTests(MigrationsFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await ExecAsync("""
            CREATE TABLE IF NOT EXISTS ps_registrations (
                id VARCHAR(40) NOT NULL PRIMARY KEY,
                client_uri VARCHAR(255),
                server_uri VARCHAR(255),
                transport VARCHAR(40),
                outbound_auth VARCHAR(40),
                expiration INTEGER DEFAULT 3600);
            TRUNCATE ps_registrations;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task ExecAsync(string sql)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertAsync(string id, string serverUri, string? clientUri = "sip:acct@carrier.example",
        string? transport = "transport-udp")
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            "INSERT INTO ps_registrations (id, server_uri, client_uri, transport, outbound_auth) " +
            "VALUES (@Id, @Server, @Client, @Transport, @Auth)");
        cmd.Parameters.Add(new NpgsqlParameter("Id", id));
        cmd.Parameters.Add(new NpgsqlParameter("Server", serverUri));
        cmd.Parameters.Add(new NpgsqlParameter("Client", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)clientUri ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("Transport", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)transport ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("Auth", $"auth-{id}"));
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<string> ReadAsync() => TrunkRegistrationFingerprint.ReadAsync(_fixture.DataSource, CancellationToken.None);

    [Fact]
    public async Task ReadAsync_ShouldAnswerZeroRows_WhenTableIsEmpty()
    {
        (await ReadAsync()).Should().Be("0:");
    }

    [Fact]
    public async Task ReadAsync_ShouldChange_WhenARowIsAddedChangedOrRemoved()
    {
        await InsertAsync("reg-t-1", "sip:carrier.example");
        var one = await ReadAsync();
        one.Should().StartWith("1:");
        (await ReadAsync()).Should().Be(one, "an unchanged table reads the same fingerprint");

        await InsertAsync("reg-t-2", "sip:other.example", clientUri: null, transport: null);
        var two = await ReadAsync();
        two.Should().StartWith("2:");

        await ExecAsync("UPDATE ps_registrations SET server_uri = 'sip:carrier.example:5070' WHERE id = 'reg-t-1'");
        var changed = await ReadAsync();
        changed.Should().StartWith("2:").And.NotBe(two);

        await ExecAsync("UPDATE ps_registrations SET expiration = 60 WHERE id = 'reg-t-1'");
        (await ReadAsync()).Should().Be(changed, "columns the sync does not write are not part of the fingerprint");

        await ExecAsync("DELETE FROM ps_registrations WHERE id = 'reg-t-2'");
        (await ReadAsync()).Should().StartWith("1:").And.NotBe(one);
    }
}
