using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// The role/status write contract of <see cref="PostgresUserStore"/> against a real Postgres:
/// <see cref="PostgresUserStore.SaveAsync"/> inserts a new user with every column but its upsert
/// never writes <c>role</c> or <c>status</c> for an existing row, which only
/// <see cref="PostgresUserStore.SetRoleAsync"/> and <see cref="PostgresUserStore.SetStatusAsync"/>
/// change. Every other user write loads the row, changes its own fields and saves the whole object
/// with no concurrency check, so a save from an object read before a suspension used to put
/// <c>Active</c> back.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresUserStoreAccessWriteTests
    : IClassFixture<UserMfaEncryptionFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset ChangedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly UserMfaEncryptionFixture _fixture;
    private readonly PostgresUserStore _sut;
    private readonly TenantId _tenant;

    public PostgresUserStoreAccessWriteTests(UserMfaEncryptionFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresUserStore(_fixture.DataSource, _fixture.DataProtection);
        _tenant = new TenantId($"t-{Guid.NewGuid():N}");
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedTenantAsync(_tenant.Value);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── A save from an earlier snapshot ────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ShouldKeepTheSuspension_WhenAnEarlierSnapshotIsSavedAfterIt()
    {
        var id = EntityId.From("u-status");
        await _sut.SaveAsync(NewUser(id), default);
        var earlierSnapshot = (await _sut.GetByIdAsync(_tenant, id, default))!;

        await _sut.SetStatusAsync(_tenant, id, UserStatus.Suspended, ChangedAt, default);

        earlierSnapshot.FailedLoginAttempts++;
        await _sut.SaveAsync(earlierSnapshot, default);

        var reloaded = (await _sut.GetByIdAsync(_tenant, id, default))!;
        reloaded.Status.Should().Be(UserStatus.Suspended,
            because: "a save from a snapshot read before the suspension must not undo it");
        reloaded.FailedLoginAttempts.Should().Be(1, because: "the snapshot's own change is still saved");
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepTheRoleChange_WhenAnEarlierSnapshotIsSavedAfterIt()
    {
        var id = EntityId.From("u-role");
        await _sut.SaveAsync(NewUser(id, UserRole.Admin), default);
        var earlierSnapshot = (await _sut.GetByIdAsync(_tenant, id, default))!;

        await _sut.SetRoleAsync(_tenant, id, UserRole.Agent, ChangedAt, default);

        earlierSnapshot.FailedLoginAttempts++;
        await _sut.SaveAsync(earlierSnapshot, default);

        (await _sut.GetByIdAsync(_tenant, id, default))!.Role.Should().Be(UserRole.Agent,
            because: "a save from a snapshot read before the demotion must not restore the old role");
    }

    // ─── SaveAsync never writes role or status of an existing user ──────────

    [Fact]
    public async Task SaveAsync_ShouldNotChangeRoleOrStatus_WhenAnExistingUserIsSavedWithDifferentValues()
    {
        var id = EntityId.From("u-direct");
        await _sut.SaveAsync(NewUser(id), default);

        var loaded = (await _sut.GetByIdAsync(_tenant, id, default))!;
        loaded.Status = UserStatus.Deactivated;
        loaded.Role = UserRole.Admin;
        loaded.DisplayName = "Renamed";
        await _sut.SaveAsync(loaded, default);

        var reloaded = (await _sut.GetByIdAsync(_tenant, id, default))!;
        reloaded.Status.Should().Be(UserStatus.Active, because: "only SetStatusAsync writes the status of an existing user");
        reloaded.Role.Should().Be(UserRole.Agent, because: "only SetRoleAsync writes the role of an existing user");
        reloaded.DisplayName.Should().Be("Renamed", because: "every other column is still saved");
    }

    [Fact]
    public async Task SaveAsync_ShouldStoreRoleAndStatus_WhenTheUserIsNew()
    {
        var id = EntityId.From("u-new");

        await _sut.SaveAsync(NewUser(id, UserRole.Supervisor, UserStatus.Suspended), default);

        var stored = (await _sut.GetByIdAsync(_tenant, id, default))!;
        stored.Role.Should().Be(UserRole.Supervisor);
        stored.Status.Should().Be(UserStatus.Suspended);
    }

    // ─── SetStatusAsync / SetRoleAsync ──────────────────────────────────────

    [Fact]
    public async Task SetStatusAsync_ShouldReturnTheStatusItReplaced_WhenTheUserExists()
    {
        var id = EntityId.From("u-set-status");
        await _sut.SaveAsync(NewUser(id), default);

        var previous = await _sut.SetStatusAsync(_tenant, id, UserStatus.Suspended, ChangedAt, default);

        previous.Should().Be(UserStatus.Active);
        var stored = (await _sut.GetByIdAsync(_tenant, id, default))!;
        stored.Status.Should().Be(UserStatus.Suspended);
        stored.UpdatedAt.Should().Be(ChangedAt);
    }

    [Fact]
    public async Task SetStatusAsync_ShouldReturnNullAndCreateNothing_WhenTheUserDoesNotExist()
    {
        var id = EntityId.From("u-missing");

        var previous = await _sut.SetStatusAsync(_tenant, id, UserStatus.Suspended, ChangedAt, default);

        previous.Should().BeNull();
        (await _sut.GetByIdAsync(_tenant, id, default)).Should().BeNull();
    }

    [Fact]
    public async Task SetRoleAsync_ShouldReturnTheRoleItReplaced_WhenTheUserExists()
    {
        var id = EntityId.From("u-set-role");
        await _sut.SaveAsync(NewUser(id, UserRole.Admin), default);

        var previous = await _sut.SetRoleAsync(_tenant, id, UserRole.Agent, ChangedAt, default);

        previous.Should().Be(UserRole.Admin);
        var stored = (await _sut.GetByIdAsync(_tenant, id, default))!;
        stored.Role.Should().Be(UserRole.Agent);
        stored.UpdatedAt.Should().Be(ChangedAt);
    }

    [Fact]
    public async Task SetRoleAsync_ShouldReturnNullAndCreateNothing_WhenTheUserDoesNotExist()
    {
        var id = EntityId.From("u-missing-role");

        var previous = await _sut.SetRoleAsync(_tenant, id, UserRole.Admin, ChangedAt, default);

        previous.Should().BeNull();
        (await _sut.GetByIdAsync(_tenant, id, default)).Should().BeNull();
    }

    [Fact]
    public async Task SetStatusAsync_ShouldReturnTheCommittedStatus_WhenAnotherWriterHeldTheRow()
    {
        // The admin endpoint revokes and audits according to the status this call says it replaced.
        // Another transaction changes the row first and holds it; the write must wait for it and
        // report that writer's committed value, not the value the row had when this call started.
        var id = EntityId.From("u-race");
        await _sut.SaveAsync(NewUser(id), default);

        await using var otherWriter = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await otherWriter.BeginTransactionAsync();
        await using (var update = new NpgsqlCommand(
            "UPDATE users SET status = @Status WHERE tenant_id = @TenantId AND user_id = @UserId", otherWriter, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter("Status", NpgsqlDbType.Integer) { Value = (int)UserStatus.Deactivated });
            update.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = _tenant.Value });
            update.Parameters.Add(new NpgsqlParameter("UserId", NpgsqlDbType.Text) { Value = id.Value });
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var write = _sut.SetStatusAsync(_tenant, id, UserStatus.Suspended, ChangedAt, default);
        await WaitUntilALockIsAwaitedAsync();
        write.IsCompleted.Should().BeFalse(because: "the row is held by the other writer");
        await transaction.CommitAsync();

        (await write).Should().Be(UserStatus.Deactivated);
        (await _sut.GetByIdAsync(_tenant, id, default))!.Status.Should().Be(UserStatus.Suspended);
    }

    private async Task WaitUntilALockIsAwaitedAsync()
    {
        // This class owns its container, so any lock request still waiting is the write under test.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using var probe = _fixture.DataSource.CreateCommand("SELECT count(*) FROM pg_locks WHERE NOT granted");
            if ((long)(await probe.ExecuteScalarAsync())! > 0)
                return;
            await Task.Delay(20); // fence-allow: LOOP-DRIVER — poll pacing until pg_locks shows the write waiting on the row
        }

        throw new TimeoutException("The write never waited for the row the other transaction holds.");
    }

    private User NewUser(EntityId id, UserRole role = UserRole.Agent, UserStatus status = UserStatus.Active) => new()
    {
        UserId = id,
        TenantId = _tenant,
        Email = $"{id.Value}@users.test",
        DisplayName = id.Value,
        Role = role,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
