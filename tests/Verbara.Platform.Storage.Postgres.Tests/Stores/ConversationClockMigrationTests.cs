using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// conversations-close-after-inactivity task 1.4 (design D2, D11): migration <c>026</c> against a real Postgres built
/// from the real migrations, and the store's handling of the new columns. <c>last_message_at</c> is backfilled from
/// the newest <b>activity message</b> (an <c>Inbound</c> message, or an <c>Outbound</c> one not stored <c>Failed</c>),
/// else <c>created_at</c>; it is <c>NOT NULL</c> with a <c>now()</c> default for an older binary's insert; the store
/// starts it at the creation and its whole-row save never rolls it back. <c>tenant_auth_config.inactivity_close_after_hours</c>
/// is nullable, read by <c>GetAsync</c> and never written by <c>SaveAsync</c>. Live-DB (Testcontainers): runs in CI's
/// "Live-DB Tests (Postgres)" job.
/// </summary>
public sealed class ConversationClockMigrationTests : IClassFixture<ConversationClockFixture>
{
    private const string Tenant = "t-clock";
    private const int Active = (int)ConversationState.Active;
    private const int Closed = (int)ConversationState.Closed;
    private const int Inbound = (int)MessageDirection.Inbound;
    private const int Outbound = (int)MessageDirection.Outbound;
    private const int SystemDirection = (int)MessageDirection.System;
    private const int Delivered = (int)MessageDeliveryStatus.Delivered;
    private const int Failed = (int)MessageDeliveryStatus.Failed;

    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly ConversationClockFixture _fixture;

    public ConversationClockMigrationTests(ConversationClockFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration026_ShouldBackfillTheNewestInboundMessage_WhenAnOpenConversationIsOlderThanItsLastMessage()
    {
        var ds = await _fixture.CreateDatabaseAsync(before026: true);
        await ConversationClockFixture.InsertLegacyConversationAsync(ds, Tenant, "c-open", Active, Now.AddDays(-5));
        await ConversationClockFixture.InsertMessageAsync(ds, Tenant, "c-open", Inbound, Delivered, Now.AddDays(-4));
        await ConversationClockFixture.InsertMessageAsync(ds, Tenant, "c-open", Inbound, Delivered, Now.AddHours(-1));

        await ConversationClockFixture.ApplyMigrationAsync(ds, ConversationClockFixture.Migration026);

        (await ConversationClockFixture.LastMessageAtAsync(ds, Tenant, "c-open")).Should().Be(Now.AddHours(-1));
    }

    [Fact]
    public async Task Migration026_ShouldIgnoreSystemAndFailedOutboundMessages_WhenTheyAreTheNewest()
    {
        var ds = await _fixture.CreateDatabaseAsync(before026: true);
        await ConversationClockFixture.InsertLegacyConversationAsync(ds, Tenant, "c-sys", Active, Now.AddDays(-5));
        await ConversationClockFixture.InsertMessageAsync(ds, Tenant, "c-sys", Inbound, Delivered, Now.AddDays(-3));
        await ConversationClockFixture.InsertMessageAsync(ds, Tenant, "c-sys", SystemDirection, Delivered, Now.AddHours(-1));
        await ConversationClockFixture.InsertLegacyConversationAsync(ds, Tenant, "c-refused", Active, Now.AddDays(-5));
        await ConversationClockFixture.InsertMessageAsync(ds, Tenant, "c-refused", Outbound, Delivered, Now.AddDays(-2));
        await ConversationClockFixture.InsertMessageAsync(ds, Tenant, "c-refused", Outbound, Failed, Now.AddHours(-1));

        await ConversationClockFixture.ApplyMigrationAsync(ds, ConversationClockFixture.Migration026);

        (await ConversationClockFixture.LastMessageAtAsync(ds, Tenant, "c-sys")).Should().Be(Now.AddDays(-3),
            "a System message (a note, a transfer note) is not customer-facing activity");
        (await ConversationClockFixture.LastMessageAtAsync(ds, Tenant, "c-refused")).Should().Be(Now.AddDays(-2),
            "an Outbound message stored Failed (refused before the send) is not activity (D2 activity rule)");
    }

    [Fact]
    public async Task Migration026_ShouldUseCreatedAt_WhenTheRowIsOpenWithoutMessagesOrTerminal()
    {
        var ds = await _fixture.CreateDatabaseAsync(before026: true);
        await ConversationClockFixture.InsertLegacyConversationAsync(ds, Tenant, "c-empty", Active, Now.AddDays(-5));
        await ConversationClockFixture.InsertLegacyConversationAsync(ds, Tenant, "c-closed", Closed, Now.AddDays(-6));
        await ConversationClockFixture.InsertMessageAsync(ds, Tenant, "c-closed", Inbound, Delivered, Now.AddHours(-1));

        await ConversationClockFixture.ApplyMigrationAsync(ds, ConversationClockFixture.Migration026);

        (await ConversationClockFixture.LastMessageAtAsync(ds, Tenant, "c-empty")).Should().Be(Now.AddDays(-5));
        (await ConversationClockFixture.LastMessageAtAsync(ds, Tenant, "c-closed")).Should().Be(Now.AddDays(-6),
            "a terminal row gets created_at, which no sweep reads");
    }

    [Fact]
    public async Task Migration026_ShouldMakeTheColumnNotNullWithANowDefault_WhenAnOlderBinaryInsertsWithoutIt()
    {
        var ds = await _fixture.CreateDatabaseAsync();

        var nullable = await ConversationClockFixture.ScalarAsync(ds,
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'conversations' AND column_name = 'last_message_at'");
        nullable.Should().Be("NO");

        var before = (DateTime)(await ConversationClockFixture.ScalarAsync(ds, "SELECT now()"))!;
        await ConversationClockFixture.InsertLegacyConversationAsync(ds, Tenant, "c-old-binary", Active, Now.AddDays(-30));

        var stored = await ConversationClockFixture.LastMessageAtAsync(ds, Tenant, "c-old-binary");
        stored.Should().NotBeNull();
        stored!.Value.Should().BeOnOrAfter(new DateTimeOffset(before.ToUniversalTime()),
            "an insert that omits the column (an old binary during a rolling deploy) gets now()");
    }

    [Fact]
    public async Task Migration026_ShouldRunTwiceWithoutError_WhenReapplied()
    {
        var ds = await _fixture.CreateDatabaseAsync(before026: true);
        await ConversationClockFixture.InsertLegacyConversationAsync(ds, Tenant, "c-twice", Active, Now.AddDays(-5));
        await ConversationClockFixture.InsertMessageAsync(ds, Tenant, "c-twice", Inbound, Delivered, Now.AddHours(-2));
        await ConversationClockFixture.ApplyMigrationAsync(ds, ConversationClockFixture.Migration026);

        var again = () => ConversationClockFixture.ApplyMigrationAsync(ds, ConversationClockFixture.Migration026);

        await again.Should().NotThrowAsync("every step is IF NOT EXISTS or idempotent");
        (await ConversationClockFixture.LastMessageAtAsync(ds, Tenant, "c-twice")).Should().Be(Now.AddHours(-2));
        (await ConversationClockFixture.ScalarAsync(ds,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'idx_conversations_state_last_message'")).Should().Be(1L);
    }

    [Fact]
    public void Migration026_ShouldBackfillWithTheStoreActivityPredicate_WhenRead()
    {
        ConversationClockFixture.MigrationSql(ConversationClockFixture.Migration026)
            .Should().Contain(PostgresConversationStore.ActivityMessagePredicate,
                "the backfill, the reconcile and the upgrade note's dry-run use one activity rule (D2)");
    }

    [Fact]
    public async Task SaveAsync_ShouldStartTheClockAtCreatedAt_WhenANewConversationHasNoLastMessageAt()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var store = new PostgresConversationStore(ds);
        var conversation = NewConversation(Now.AddHours(-3));

        await store.SaveAsync(conversation, CancellationToken.None);

        (await ConversationClockFixture.LastMessageAtAsync(ds, Tenant, conversation.ConversationId.Value))
            .Should().Be(Now.AddHours(-3));
        (await store.GetByIdAsync(new TenantId(Tenant), conversation.ConversationId, CancellationToken.None))!
            .LastMessageAt.Should().Be(Now.AddHours(-3));
    }

    [Fact]
    public async Task SaveAsync_ShouldStartTheClockAtLastMessageAt_WhenANewConversationIsBornWithAMessage()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var store = new PostgresConversationStore(ds);
        var conversation = NewConversation(Now.AddHours(-3));
        conversation.LastMessageAt = Now.AddHours(-2);

        await store.SaveAsync(conversation, CancellationToken.None);

        (await ConversationClockFixture.LastMessageAtAsync(ds, Tenant, conversation.ConversationId.Value))
            .Should().Be(Now.AddHours(-2));
    }

    [Fact]
    public async Task SaveAsync_ShouldNotRollTheClockBack_WhenAStaleEntityIsSavedWhole()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var store = new PostgresConversationStore(ds);
        var conversation = NewConversation(Now.AddDays(-2));
        await store.SaveAsync(conversation, CancellationToken.None);
        var stale = await store.GetByIdAsync(new TenantId(Tenant), conversation.ConversationId, CancellationToken.None);
        await ConversationClockFixture.ExecuteAsync(ds,
            "UPDATE conversations SET last_message_at = @At WHERE conversation_id = @Id",
            new NpgsqlParameter("At", NpgsqlDbType.TimestampTz) { Value = Now.AddMinutes(-5) },
            new NpgsqlParameter("Id", conversation.ConversationId.Value));

        stale!.State = ConversationState.OnHold;
        stale.UpdatedAt = Now;
        await store.SaveAsync(stale, CancellationToken.None);

        (await ConversationClockFixture.LastMessageAtAsync(ds, Tenant, conversation.ConversationId.Value))
            .Should().Be(Now.AddMinutes(-5), "the whole-row save never writes the clock (D2)");
    }

    // ── D11: tenant_auth_config.inactivity_close_after_hours ──────────────────────────────

    [Fact]
    public async Task Migration026_ShouldAddANullableOverrideThatIsNullForEveryExistingRow_WhenApplied()
    {
        var ds = await _fixture.CreateDatabaseAsync(before026: true);
        await ConversationClockFixture.ExecuteAsync(ds,
            "INSERT INTO tenant_auth_config (tenant_id) VALUES ('t-a'), ('t-b')");

        await ConversationClockFixture.ApplyMigrationAsync(ds, ConversationClockFixture.Migration026);

        (await ConversationClockFixture.ScalarAsync(ds,
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'tenant_auth_config' AND column_name = 'inactivity_close_after_hours'"))
            .Should().Be("YES");
        (await ConversationClockFixture.ScalarAsync(ds,
            "SELECT count(*) FROM tenant_auth_config WHERE inactivity_close_after_hours IS NOT NULL")).Should().Be(0L);
    }

    [Fact]
    public async Task TenantAuthConfigGetAsync_ShouldReadTheOverride_WhenSetBySql()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var store = new PostgresTenantAuthConfigStore(ds, _fixture.DataProtection);
        await store.SaveAsync(new TenantAuthConfig { TenantId = "t-48" }, CancellationToken.None);
        await store.SaveAsync(new TenantAuthConfig { TenantId = "t-null" }, CancellationToken.None);
        await SetOverrideAsync(ds, "t-48", 48);

        (await store.GetAsync("t-48", CancellationToken.None))!.InactivityCloseAfterHours.Should().Be(48);
        (await store.GetAsync("t-null", CancellationToken.None))!.InactivityCloseAfterHours.Should().BeNull();
    }

    [Fact]
    public async Task TenantAuthConfigSaveAsync_ShouldNeverWriteTheOverride_WhenTheEntityCarriesOne()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var store = new PostgresTenantAuthConfigStore(ds, _fixture.DataProtection);

        await store.SaveAsync(new TenantAuthConfig { TenantId = "t-new", InactivityCloseAfterHours = 24 }, CancellationToken.None);
        (await OverrideAsync(ds, "t-new")).Should().BeNull("a new row has no override (D11)");

        await SetOverrideAsync(ds, "t-new", 48);
        var loaded = await store.GetAsync("t-new", CancellationToken.None);
        loaded!.InactivityCloseAfterHours = 24;
        loaded.PasswordMinLength = 16;
        await store.SaveAsync(loaded, CancellationToken.None);

        (await OverrideAsync(ds, "t-new")).Should().Be(48, "the whole-row save never writes the column (D11)");
        (await store.GetAsync("t-new", CancellationToken.None))!.PasswordMinLength.Should().Be(16);
    }

    [Fact]
    public async Task TenantAuthConfigUpsert_ShouldKeepTheOverride_WhenAnOlderBinaryUpsertsWithoutTheColumn()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        await ConversationClockFixture.ExecuteAsync(ds, "INSERT INTO tenant_auth_config (tenant_id) VALUES ('t-old')");
        await SetOverrideAsync(ds, "t-old", 48);

        await ConversationClockFixture.ExecuteAsync(ds,
            "INSERT INTO tenant_auth_config (tenant_id, password_min_length) VALUES ('t-old', 14) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET password_min_length = EXCLUDED.password_min_length");

        (await OverrideAsync(ds, "t-old")).Should().Be(48);
    }

    private static Conversation NewConversation(DateTimeOffset createdAt) => new()
    {
        ConversationId = EntityId.From($"c-{Guid.NewGuid():N}"),
        TenantId = new TenantId(Tenant),
        ContactId = EntityId.From("contact-1"),
        Channel = ChannelType.WhatsApp,
        State = ConversationState.Active,
        Owner = ConversationOwner.ForAgent(EntityId.From("agent-1")),
        CreatedAt = createdAt,
    };

    private static Task SetOverrideAsync(NpgsqlDataSource ds, string tenantId, int? hours) =>
        ConversationClockFixture.ExecuteAsync(ds,
            "UPDATE tenant_auth_config SET inactivity_close_after_hours = @Hours WHERE tenant_id = @TenantId",
            new NpgsqlParameter("Hours", NpgsqlDbType.Integer) { Value = (object?)hours ?? DBNull.Value },
            new NpgsqlParameter("TenantId", tenantId));

    private static async Task<int?> OverrideAsync(NpgsqlDataSource ds, string tenantId) =>
        (int?)await ConversationClockFixture.ScalarAsync(ds,
            "SELECT inactivity_close_after_hours FROM tenant_auth_config WHERE tenant_id = @TenantId",
            new NpgsqlParameter("TenantId", tenantId));
}
