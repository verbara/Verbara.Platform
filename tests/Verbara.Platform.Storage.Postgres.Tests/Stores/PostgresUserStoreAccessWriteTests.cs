using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// The insert and admin-field contract of <see cref="PostgresUserStore"/> against a real Postgres:
/// <see cref="PostgresUserStore.CreateAsync"/> is a plain INSERT that never lands on an existing row,
/// and <see cref="PostgresUserStore.UpdateAdminFieldsAsync"/> — the only writer of role and status —
/// changes an existing row in one statement that reports, under the row lock, the values it replaced.
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

    // ─── CreateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ShouldStoreRoleAndStatus_WhenTheUserIsNew()
    {
        var id = EntityId.From("u-new");

        await _sut.CreateAsync(NewUser(id, UserRole.Supervisor, UserStatus.Suspended), default);

        var stored = (await _sut.GetByIdAsync(_tenant, id, default))!;
        stored.Role.Should().Be(UserRole.Supervisor);
        stored.Status.Should().Be(UserStatus.Suspended);
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowAndChangeNothing_WhenTheIdIsTaken()
    {
        var id = EntityId.From("u-taken");
        await _sut.CreateAsync(NewUser(id), default);
        var again = NewUser(id, UserRole.Admin);
        var otherEmail = new User
        {
            UserId = id,
            TenantId = _tenant,
            Email = "someone-else@users.test",
            DisplayName = again.DisplayName,
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = again.CreatedAt,
        };

        var create = () => _sut.CreateAsync(otherEmail, default);

        (await create.Should().ThrowAsync<EntityAlreadyExistsException>()).Which.ConflictingField.Should().BeNull();
        (await _sut.GetByIdAsync(_tenant, id, default))!.Role.Should().Be(UserRole.Agent,
            because: "an insert never updates the row it collides with");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowWithTheEmailField_WhenAnotherUserHasTheEmail()
    {
        await _sut.CreateAsync(NewUser(EntityId.From("u-first")), default);
        var duplicate = new User
        {
            UserId = EntityId.From("u-second"),
            TenantId = _tenant,
            Email = "U-FIRST@users.test",
            DisplayName = "u-second",
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var create = () => _sut.CreateAsync(duplicate, default);

        (await create.Should().ThrowAsync<EntityAlreadyExistsException>()).Which.ConflictingField.Should().Be("email");
        (await _sut.GetByIdAsync(_tenant, duplicate.UserId, default)).Should().BeNull();
    }

    // ─── UpdateAdminFieldsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateAdminFieldsAsync_ShouldReturnTheStatusItReplaced_WhenTheUserExists()
    {
        var id = EntityId.From("u-set-status");
        await _sut.CreateAsync(NewUser(id), default);

        var result = await _sut.UpdateAdminFieldsAsync(
            _tenant, id, new AdminFieldsChange { Status = UserStatus.Suspended }, ChangedAt, "admin-1", default);

        result.Outcome.Should().Be(AdminFieldsWriteOutcome.Written);
        result.Previous!.Value.Status.Should().Be(UserStatus.Active);
        result.User!.Status.Should().Be(UserStatus.Suspended);
        var stored = (await _sut.GetByIdAsync(_tenant, id, default))!;
        stored.Status.Should().Be(UserStatus.Suspended);
        stored.UpdatedAt.Should().Be(ChangedAt);
        stored.UpdatedBy.Should().Be("admin-1");
    }

    [Fact]
    public async Task UpdateAdminFieldsAsync_ShouldReturnTheRoleItReplaced_WhenTheUserExists()
    {
        var id = EntityId.From("u-set-role");
        await _sut.CreateAsync(NewUser(id, UserRole.Admin), default);

        var result = await _sut.UpdateAdminFieldsAsync(
            _tenant, id, new AdminFieldsChange { Role = UserRole.Agent }, ChangedAt, null, default);

        result.Previous!.Value.Role.Should().Be(UserRole.Admin);
        var stored = (await _sut.GetByIdAsync(_tenant, id, default))!;
        stored.Role.Should().Be(UserRole.Agent);
        stored.UpdatedAt.Should().Be(ChangedAt);
    }

    [Fact]
    public async Task UpdateAdminFieldsAsync_ShouldWriteOnlyTheGivenFields_WhenSomeAreLeftNull()
    {
        var id = EntityId.From("u-partial");
        var user = NewUser(id, UserRole.Admin);
        user.PasswordHash = "hash";
        user.FailedLoginAttempts = 2;
        await _sut.CreateAsync(user, default);

        await _sut.UpdateAdminFieldsAsync(
            _tenant, id, new AdminFieldsChange { DisplayName = "Renamed" }, ChangedAt, null, default);

        var stored = (await _sut.GetByIdAsync(_tenant, id, default))!;
        stored.DisplayName.Should().Be("Renamed");
        stored.Role.Should().Be(UserRole.Admin);
        stored.Status.Should().Be(UserStatus.Active);
        stored.PasswordHash.Should().Be("hash", because: "no other column is written");
        stored.FailedLoginAttempts.Should().Be(2);
    }

    [Fact]
    public async Task UpdateAdminFieldsAsync_ShouldReturnNotFoundAndCreateNothing_WhenTheUserDoesNotExist()
    {
        var id = EntityId.From("u-missing");

        var result = await _sut.UpdateAdminFieldsAsync(
            _tenant, id, new AdminFieldsChange { Status = UserStatus.Suspended }, ChangedAt, null, default);

        result.Outcome.Should().Be(AdminFieldsWriteOutcome.NotFound);
        (await _sut.GetByIdAsync(_tenant, id, default)).Should().BeNull();
    }

    [Fact]
    public async Task UpdateAdminFieldsAsync_ShouldReturnStaleAndChangeNothing_WhenTheRowNoLongerMatchesTheExpectation()
    {
        // An admin's form showed Admin; another admin demoted the user since. The stale form's write
        // must not put Admin back.
        var id = EntityId.From("u-stale");
        await _sut.CreateAsync(NewUser(id, UserRole.Admin), default);
        var seen = new AdminFields(id.Value, UserRole.Admin, UserStatus.Active);
        await _sut.UpdateAdminFieldsAsync(_tenant, id, new AdminFieldsChange { Role = UserRole.Agent }, ChangedAt, null, default);

        var result = await _sut.UpdateAdminFieldsAsync(
            _tenant, id, new AdminFieldsChange { DisplayName = "Stale form", Role = UserRole.Admin, Expected = seen },
            ChangedAt.AddMinutes(1), null, default);

        result.Outcome.Should().Be(AdminFieldsWriteOutcome.Stale);
        var stored = (await _sut.GetByIdAsync(_tenant, id, default))!;
        stored.Role.Should().Be(UserRole.Agent);
        stored.DisplayName.Should().Be(id.Value);
        stored.UpdatedAt.Should().Be(ChangedAt, because: "a refused write changes nothing at all");
    }

    [Fact]
    public async Task UpdateAdminFieldsAsync_ShouldWrite_WhenTheRowStillMatchesTheExpectation()
    {
        var id = EntityId.From("u-current");
        await _sut.CreateAsync(NewUser(id), default);

        var result = await _sut.UpdateAdminFieldsAsync(
            _tenant, id,
            new AdminFieldsChange { Status = UserStatus.Deactivated, Expected = new AdminFields(id.Value, UserRole.Agent, UserStatus.Active) },
            ChangedAt, null, default);

        result.Outcome.Should().Be(AdminFieldsWriteOutcome.Written);
        (await _sut.GetByIdAsync(_tenant, id, default))!.Status.Should().Be(UserStatus.Deactivated);
    }

    [Fact]
    public async Task UpdateAdminFieldsAsync_ShouldReturnNotFound_WhenAnExpectedUserWasDeleted()
    {
        var id = EntityId.From("u-gone");
        await _sut.CreateAsync(NewUser(id), default);
        await _sut.DeleteAsync(_tenant, id, default);

        var result = await _sut.UpdateAdminFieldsAsync(
            _tenant, id,
            new AdminFieldsChange { Role = UserRole.Admin, Expected = new AdminFields(id.Value, UserRole.Agent, UserStatus.Active) },
            ChangedAt, null, default);

        result.Outcome.Should().Be(AdminFieldsWriteOutcome.NotFound);
        (await _sut.GetByIdAsync(_tenant, id, default)).Should().BeNull();
    }

    [Fact]
    public async Task UpdateAdminFieldsAsync_ShouldReturnTheCommittedValues_WhenAnotherWriterHeldTheRow()
    {
        // The admin endpoint revokes and audits according to the values this call says it replaced.
        // Another transaction changes the row first and holds it; the write must wait for it and
        // report that writer's committed value, not the value the row had when this call started.
        var id = EntityId.From("u-race");
        await _sut.CreateAsync(NewUser(id), default);

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

        var write = _sut.UpdateAdminFieldsAsync(
            _tenant, id, new AdminFieldsChange { Status = UserStatus.Suspended }, ChangedAt, null, default);
        await WaitUntilALockIsAwaitedAsync();
        write.IsCompleted.Should().BeFalse(because: "the row is held by the other writer");
        await transaction.CommitAsync();

        (await write).Previous!.Value.Status.Should().Be(UserStatus.Deactivated);
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
