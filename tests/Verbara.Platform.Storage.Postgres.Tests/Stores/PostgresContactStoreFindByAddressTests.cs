using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Address lookup against a real Postgres <c>contacts.addresses</c> JSONB column. Since 2.13.0
/// <see cref="PostgresContactStore.SaveAsync"/> writes <c>"channel"</c> as the enum name
/// (<c>"WhatsApp"</c>); rows written before that hold the ordinal (<c>1</c>). The lookup must
/// match both forms; a name-form row must never make a lookup throw (22P02) — before 2.26.1
/// every lookup whose address matched a name-form row of the tenant did, on any channel.
/// </summary>
public class PostgresContactStoreFindByAddressTests : IClassFixture<ContactStoreFixture>, IAsyncLifetime
{
    private readonly ContactStoreFixture _fixture;
    private readonly PostgresContactStore _sut;
    private readonly TenantId _tenant;

    public PostgresContactStoreFindByAddressTests(ContactStoreFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresContactStore(_fixture.DataSource);
        _tenant = new TenantId($"t-{Guid.NewGuid():N}");
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Contact MakeContact(TenantId tenant, params ChannelAddress[] addresses)
    {
        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        foreach (var address in addresses)
            contact.AddAddress(address);
        return contact;
    }

    /// <summary>Writes a contact row the way releases before 2.13.0 did: ordinal channels.</summary>
    private async Task<EntityId> InsertOrdinalRowAsync(TenantId tenant, string addressesJson)
    {
        var contactId = EntityId.New();
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO contacts (contact_id, tenant_id, addresses, created_at) " +
            "VALUES (@ContactId, @TenantId, @Addresses::jsonb, now())";
        cmd.Parameters.Add(new NpgsqlParameter("ContactId", NpgsqlDbType.Text) { Value = contactId.Value });
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenant.Value });
        cmd.Parameters.Add(new NpgsqlParameter("Addresses", NpgsqlDbType.Text) { Value = addressesJson });
        await cmd.ExecuteNonQueryAsync();
        return contactId;
    }

    private async Task<string> ReadStoredAddressesAsync(EntityId contactId)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT addresses::text FROM contacts WHERE tenant_id = @TenantId AND contact_id = @ContactId";
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = _tenant.Value });
        cmd.Parameters.Add(new NpgsqlParameter("ContactId", NpgsqlDbType.Text) { Value = contactId.Value });
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task SaveAsync_ShouldStoreChannelAsName_WhenAddressSaved()
    {
        // Pins the precondition the lookup tests depend on: if SaveAsync ever went back to
        // ordinals, the name-form tests below would pass without exercising the mixed case.
        var contact = MakeContact(_tenant, new ChannelAddress(ChannelType.WhatsApp, "+5215512345678"));
        await _sut.SaveAsync(contact, CancellationToken.None);

        var stored = await ReadStoredAddressesAsync(contact.ContactId);

        stored.Should().Contain("\"channel\": \"WhatsApp\"");
    }

    [Fact]
    public async Task FindByAddressAsync_ShouldReturnContact_WhenAddressSavedByThisRelease()
    {
        var address = new ChannelAddress(ChannelType.WhatsApp, "+5215512345678");
        var contact = MakeContact(_tenant, address);
        await _sut.SaveAsync(contact, CancellationToken.None);

        var found = await _sut.FindByAddressAsync(_tenant, address, CancellationToken.None);

        found.Should().NotBeNull();
        found!.ContactId.Should().Be(contact.ContactId);
        found.Addresses.Should().ContainSingle().Which.Should().Be(address);
    }

    [Fact]
    public async Task FindByAddressAsync_ShouldReturnContact_WhenRowStoredWithOrdinalChannel()
    {
        var contactId = await InsertOrdinalRowAsync(
            _tenant, """[{"channel": 1, "address": "+5215512345678"}]""");

        var found = await _sut.FindByAddressAsync(
            _tenant, new ChannelAddress(ChannelType.WhatsApp, "+5215512345678"), CancellationToken.None);

        found.Should().NotBeNull();
        found!.ContactId.Should().Be(contactId);
        found.Addresses.Should().ContainSingle()
            .Which.Should().Be(new ChannelAddress(ChannelType.WhatsApp, "+5215512345678"));
    }

    [Fact]
    public async Task FindByAddressAsync_ShouldReturnNull_WhenAnotherContactInTenantHasNameChannel()
    {
        // Another contact of the tenant holds the same address string on another channel, in
        // name form. Postgres evaluates the cheaper address comparison first, so the channel
        // comparison runs on exactly the rows whose address matches — this row included.
        await _sut.SaveAsync(
            MakeContact(_tenant, new ChannelAddress(ChannelType.Sms, "+5215512345678")), CancellationToken.None);

        var found = await _sut.FindByAddressAsync(
            _tenant, new ChannelAddress(ChannelType.WhatsApp, "+5215512345678"), CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task FindByAddressAsync_ShouldReturnNull_WhenNoRowOfTenantMatchesAddress()
    {
        // Control: a name-form row whose address differs from the one looked up.
        await _sut.SaveAsync(
            MakeContact(_tenant, new ChannelAddress(ChannelType.Email, "someone@example.com")),
            CancellationToken.None);

        var found = await _sut.FindByAddressAsync(
            _tenant, new ChannelAddress(ChannelType.Sms, "+5215500000000"), CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task FindByAddressAsync_ShouldReturnContact_WhenTenantMixesOrdinalAndNameRows()
    {
        var legacyId = await InsertOrdinalRowAsync(
            _tenant, """[{"channel": 2, "address": "+5215511111111"}]""");
        var current = MakeContact(_tenant, new ChannelAddress(ChannelType.Sms, "+5215522222222"));
        await _sut.SaveAsync(current, CancellationToken.None);

        var legacy = await _sut.FindByAddressAsync(
            _tenant, new ChannelAddress(ChannelType.Sms, "+5215511111111"), CancellationToken.None);
        var fresh = await _sut.FindByAddressAsync(
            _tenant, new ChannelAddress(ChannelType.Sms, "+5215522222222"), CancellationToken.None);

        legacy!.ContactId.Should().Be(legacyId);
        fresh!.ContactId.Should().Be(current.ContactId);
    }

    [Fact]
    public async Task FindByAddressAsync_ShouldReturnNull_WhenSameAddressBelongsToOtherTenant()
    {
        var address = new ChannelAddress(ChannelType.WhatsApp, "+5215512345678");
        await _sut.SaveAsync(MakeContact(new TenantId($"t-{Guid.NewGuid():N}"), address), CancellationToken.None);

        var found = await _sut.FindByAddressAsync(_tenant, address, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task FindByAddressAsync_ShouldReturnNull_WhenSameAddressStoredWithOtherOrdinalChannel()
    {
        // The channel stays part of the match for legacy rows too.
        await InsertOrdinalRowAsync(_tenant, """[{"channel": 2, "address": "+5215512345678"}]""");

        var found = await _sut.FindByAddressAsync(
            _tenant, new ChannelAddress(ChannelType.WhatsApp, "+5215512345678"), CancellationToken.None);

        found.Should().BeNull();
    }

    [Theory]
    [InlineData(ChannelType.Voice, "+5215512345678")]
    [InlineData(ChannelType.WhatsApp, "+5215512345678")]
    [InlineData(ChannelType.Sms, "+5215512345678")]
    [InlineData(ChannelType.WebChat, "webchat-0b6f3c1e")]
    [InlineData(ChannelType.Email, "Someone.Mixed@Example.com")]
    [InlineData(ChannelType.Messenger, "psid-1234567890")]
    [InlineData(ChannelType.Instagram, "igsid-1234567890")]
    [InlineData(ChannelType.Telegram, "123456789")]
    [InlineData(ChannelType.Twitter, "x-1234567890")]
    [InlineData(ChannelType.Rcs, "+5215512345678")]
    public async Task FindByAddressAsync_ShouldReturnContact_WhenChannelFamilySavedAlongsideNameRows(
        ChannelType channel, string addressValue)
    {
        // Another name-form row in the tenant first, so every family is looked up under the
        // condition that broke them all: any row of the tenant carrying a channel name.
        await _sut.SaveAsync(
            MakeContact(_tenant, new ChannelAddress(ChannelType.Email, "other@example.com")), CancellationToken.None);
        var address = new ChannelAddress(channel, addressValue);
        var contact = MakeContact(_tenant, address);
        await _sut.SaveAsync(contact, CancellationToken.None);

        var found = await _sut.FindByAddressAsync(_tenant, address, CancellationToken.None);

        found!.ContactId.Should().Be(contact.ContactId);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnSameContact_WhenSecondInboundArrivesForNameFormContact()
    {
        // Inbound path: ContactResolutionStep → DefaultContactIdentityResolver → FindByAddressAsync.
        // The first inbound creates the contact (name form); the second must resolve it, not throw.
        var resolver = new DefaultContactIdentityResolver(_sut, new SystemClock());
        var address = new ChannelAddress(ChannelType.WhatsApp, "+5215512345678");

        var first = await resolver.ResolveAsync(_tenant, address, CancellationToken.None);
        var second = await resolver.ResolveAsync(_tenant, address, CancellationToken.None);
        var otherSender = await resolver.ResolveAsync(
            _tenant, new ChannelAddress(ChannelType.WhatsApp, "+5215599999999"), CancellationToken.None);

        second.ContactId.Should().Be(first.ContactId);
        otherSender.ContactId.Should().NotBe(first.ContactId);
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM contacts WHERE tenant_id = @TenantId";
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = _tenant.Value });
        ((long)(await cmd.ExecuteScalarAsync())!).Should().Be(2);
    }
}
