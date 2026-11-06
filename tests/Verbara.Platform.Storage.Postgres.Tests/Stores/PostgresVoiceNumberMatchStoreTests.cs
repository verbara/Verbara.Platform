using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// <see cref="VoiceNumberMatchStoreContract"/> against <see cref="PostgresContactStore"/> on a real Postgres, plus the
/// rows only Postgres can hold: a Voice address whose channel was written as its ordinal (before 2.13.0) or as its name.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresVoiceNumberMatchStoreTests : VoiceNumberMatchStoreContract, IClassFixture<ContactStoreFixture>
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresVoiceNumberMatchStoreTests(ContactStoreFixture fixture)
    {
        _dataSource = fixture.DataSource;
        Store = new PostgresContactStore(fixture.DataSource);
    }

    protected override IContactStore Store { get; }

    protected override TenantId Tenant { get; } = new($"t-{Guid.NewGuid():N}");

    private async Task<string> SeedRowAsync(string channelJson, string address, bool doNotContact)
    {
        var contactId = EntityId.New().Value;
        var addresses = $$"""[{"channel":{{channelJson}},"address":"{{address}}"}]""";
        await using var cmd = _dataSource.CreateCommand(
            "INSERT INTO contacts (contact_id, tenant_id, do_not_contact, addresses, custom_fields, channel_consent, created_at) " +
            "VALUES (@ContactId, @TenantId, @DoNotContact, @Addresses::jsonb, '{}'::jsonb, '{}'::jsonb, now())");
        cmd.Parameters.Add(new NpgsqlParameter("ContactId", NpgsqlDbType.Text) { Value = contactId });
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = Tenant.Value });
        cmd.Parameters.Add(new NpgsqlParameter("DoNotContact", NpgsqlDbType.Boolean) { Value = doNotContact });
        cmd.Parameters.Add(new NpgsqlParameter("Addresses", NpgsqlDbType.Text) { Value = addresses });
        await cmd.ExecuteNonQueryAsync();
        return contactId;
    }

    [Fact]
    public async Task FindAllByVoiceNumber_ShouldMatchAVoiceRow_WhetherItsChannelIsWrittenAsNameOrOrdinal()
    {
        var byName = await SeedRowAsync("\"Voice\"", "+1 (555) 123-4567", doNotContact: true);
        var byOrdinal = await SeedRowAsync("0", "+15551234567", doNotContact: false);
        await SeedRowAsync("2", "+15551234567", doNotContact: true); // Sms, by ordinal

        var found = await FindAsync("+15551234567");

        found.Select(c => c.ContactId.Value).Should().Equal(byName, byOrdinal);
    }

    [Fact]
    public async Task FindAllByChannelAddress_ShouldMatchAWhatsAppRow_WhetherItsChannelIsWrittenAsNameOrOrdinal()
    {
        var byName = await SeedRowAsync("\"WhatsApp\"", "+1 (555) 123-4567", doNotContact: true);
        var byOrdinal = await SeedRowAsync("1", "+15551234567", doNotContact: false);
        await SeedRowAsync("0", "+15551234567", doNotContact: true); // Voice, by ordinal

        var found = await FindOnAsync(ChannelType.WhatsApp, "+15551234567");

        found.Select(c => c.ContactId.Value).Should().Equal(byName, byOrdinal);
    }

    [Fact]
    public async Task FindAllByChannelAddress_ShouldMatchAnEmailRow_WhetherItsChannelIsWrittenAsNameOrOrdinal()
    {
        var byName = await SeedRowAsync("\"Email\"", " ana@example.com", doNotContact: true);
        var byOrdinal = await SeedRowAsync("4", "ana@example.com", doNotContact: false);

        var found = await FindOnAsync(ChannelType.Email, "ana@example.com");

        found.Select(c => c.ContactId.Value).Should().Equal(byName, byOrdinal);
    }
}
