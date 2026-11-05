using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary><see cref="ContactIdentityKeyStoreContract"/> against <see cref="PostgresContactStore"/> on a real Postgres.</summary>
[Trait("Category", "Integration")]
public sealed class PostgresContactIdentityKeyStoreTests : ContactIdentityKeyStoreContract, IClassFixture<ContactStoreFixture>
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresContactIdentityKeyStoreTests(ContactStoreFixture fixture)
    {
        _dataSource = fixture.DataSource;
        Store = new PostgresContactStore(fixture.DataSource);
    }

    protected override IContactStore Store { get; }

    protected override TenantId Tenant { get; } = new($"t-{Guid.NewGuid():N}");

    // A row written before 2.13.0 carries the channel as its ordinal.
    private async Task<EntityId> SeedRowWithOrdinalChannelAsync(params string[] addresses)
    {
        var contactId = EntityId.New();
        var ordinal = (int)ChannelType.WebChat;
        var json = "[" + string.Join(',', addresses.Select(a => $$"""{"channel":{{ordinal}},"address":"{{a}}"}""")) + "]";
        await using var cmd = _dataSource.CreateCommand(
            "INSERT INTO contacts (contact_id, tenant_id, do_not_contact, addresses, custom_fields, channel_consent, created_at) " +
            "VALUES (@ContactId, @TenantId, FALSE, @Addresses::jsonb, '{}'::jsonb, '{}'::jsonb, now())");
        cmd.Parameters.Add(new NpgsqlParameter("ContactId", NpgsqlDbType.Text) { Value = contactId.Value });
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = Tenant.Value });
        cmd.Parameters.Add(new NpgsqlParameter("Addresses", NpgsqlDbType.Text) { Value = json });
        await cmd.ExecuteNonQueryAsync();
        return contactId;
    }

    [Fact]
    public async Task FindOrInsertByAddressAsync_ShouldFindTheHolder_WhenTheRowCarriesTheChannelOrdinal()
    {
        const string key = "webchat-visitor:rcfqwP4uUDSA6hgRqgUIE3hCFFsXvDuxhE3MgUu0Y1M";
        var holder = await SeedRowWithOrdinalChannelAsync(key);
        var address = new ChannelAddress(ChannelType.WebChat, key);
        var candidate = new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow };
        candidate.AddAddress(address);

        var result = await Store.FindOrInsertByAddressAsync(Tenant, address, candidate, CancellationToken.None);

        result.ContactId.Should().Be(holder);
    }

    [Fact]
    public async Task AttachWebChatSessionAsync_ShouldCountAndDropAddresses_WhenTheyCarryTheChannelOrdinal()
    {
        var seeded = Enumerable.Range(1, 50).Select(i => $"webchat-{i:x32}").ToArray();
        var holder = await SeedRowWithOrdinalChannelAsync(seeded);
        var added = new ChannelAddress(ChannelType.WebChat, $"webchat-{51:x32}");

        var result = await Store.AttachWebChatSessionAsync(Tenant, holder, added, null, DateTimeOffset.UtcNow, "system", CancellationToken.None);

        result!.Addresses.Select(a => a.Address).Should().Equal([.. seeded.Skip(1), added.Address]);
    }

    [Fact]
    public async Task AttachWebChatSessionAsync_ShouldLoseNoAddress_WhenSessionsAttachConcurrently()
    {
        var key = new ChannelAddress(ChannelType.WebChat, "webchat-visitor:rcfqwP4uUDSA6hgRqgUIE3hCFFsXvDuxhE3MgUu0Y1M");
        var contact = new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow };
        contact.AddAddress(key);
        await Store.SaveAsync(contact, CancellationToken.None);
        var sessions = Enumerable.Range(1, 8).Select(i => new ChannelAddress(ChannelType.WebChat, $"webchat-{i:x32}")).ToArray();

        await Task.WhenAll(sessions.Select(s => Task.Run(() =>
            Store.AttachWebChatSessionAsync(Tenant, contact.ContactId, s, null, DateTimeOffset.UtcNow, "system", CancellationToken.None))));

        var stored = await Store.GetByIdAsync(Tenant, contact.ContactId, CancellationToken.None);
        stored!.Addresses.Should().BeEquivalentTo([key, .. sessions]);
    }
}
