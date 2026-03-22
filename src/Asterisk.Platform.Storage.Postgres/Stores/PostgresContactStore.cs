using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresContactStore : IContactStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresContactStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Contact?> GetByIdAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ContactRow>(
            "SELECT contact_id, tenant_id, first_name, last_name, company, segment, preferred_channel, " +
            "preferred_language, timezone, do_not_contact, addresses, custom_fields, channel_consent, " +
            "created_at, updated_at, created_by, updated_by " +
            "FROM contacts WHERE tenant_id = @TenantId AND contact_id = @ContactId",
            new { TenantId = tenantId.Value, ContactId = contactId.Value });
        return row?.ToContact();
    }

    public async Task<Contact?> FindByAddressAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ContactRow>(
            "SELECT contact_id, tenant_id, first_name, last_name, company, segment, preferred_channel, " +
            "preferred_language, timezone, do_not_contact, addresses, custom_fields, channel_consent, " +
            "created_at, updated_at, created_by, updated_by " +
            "FROM contacts WHERE tenant_id = @TenantId " +
            "  AND EXISTS (SELECT 1 FROM jsonb_array_elements(addresses) AS a " +
            "              WHERE (a->>'channel')::int = @Channel AND a->>'address' = @Address)",
            new { TenantId = tenantId.Value, Channel = (int)address.Channel, Address = address.Address });
        return rows.Select(r => r.ToContact()).FirstOrDefault();
    }

    public async Task<PagedResult<Contact>> SearchAsync(TenantId tenantId, string? searchTerm, PagedQuery query, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        string filter;
        object parameters;
        if (string.IsNullOrEmpty(searchTerm))
        {
            filter = "WHERE tenant_id = @TenantId";
            parameters = new { TenantId = tenantId.Value, Limit = query.PageSize, Offset = query.Offset };
        }
        else
        {
            filter = "WHERE tenant_id = @TenantId AND (first_name ILIKE @Term OR last_name ILIKE @Term OR company ILIKE @Term)";
            parameters = new { TenantId = tenantId.Value, Term = $"%{searchTerm}%", Limit = query.PageSize, Offset = query.Offset };
        }

        var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM contacts {filter}", parameters);
        var rows = await conn.QueryAsync<ContactRow>(
            "SELECT contact_id, tenant_id, first_name, last_name, company, segment, preferred_channel, " +
            "preferred_language, timezone, do_not_contact, addresses, custom_fields, channel_consent, " +
            $"created_at, updated_at, created_by, updated_by FROM contacts {filter} ORDER BY created_at LIMIT @Limit OFFSET @Offset",
            parameters);
        var items = rows.Select(r => r.ToContact()).ToList();
        return new PagedResult<Contact>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Contact contact, CancellationToken ct)
    {
        var addressesJson = JsonSerializer.Serialize(
            contact.Addresses,
            PostgresJson.Ctx.IReadOnlyListChannelAddress);
        var customFieldsJson = JsonSerializer.Serialize(
            contact.CustomFields,
            PostgresJson.Ctx.IReadOnlyDictionaryStringString);
        var consentJson = JsonSerializer.Serialize(
            (Dictionary<int, bool>)contact.ChannelConsent.ToDictionary(kv => (int)kv.Key, kv => kv.Value),
            PostgresJson.Ctx.DictionaryInt32Boolean);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO contacts (contact_id, tenant_id, first_name, last_name, company, segment, preferred_channel, " +
            "preferred_language, timezone, do_not_contact, addresses, custom_fields, channel_consent, created_at, updated_at, created_by, updated_by) " +
            "VALUES (@ContactId, @TenantId, @FirstName, @LastName, @Company, @Segment, @PreferredChannel, " +
            "@PreferredLanguage, @Timezone, @DoNotContact, @Addresses::jsonb, @CustomFields::jsonb, @ChannelConsent::jsonb, " +
            "@CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, contact_id) DO UPDATE SET " +
            "  first_name = EXCLUDED.first_name, last_name = EXCLUDED.last_name, company = EXCLUDED.company, " +
            "  segment = EXCLUDED.segment, preferred_channel = EXCLUDED.preferred_channel, " +
            "  preferred_language = EXCLUDED.preferred_language, timezone = EXCLUDED.timezone, " +
            "  do_not_contact = EXCLUDED.do_not_contact, addresses = EXCLUDED.addresses, " +
            "  custom_fields = EXCLUDED.custom_fields, channel_consent = EXCLUDED.channel_consent, " +
            "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by",
            new
            {
                ContactId = contact.ContactId.Value,
                TenantId = contact.TenantId.Value,
                contact.FirstName,
                contact.LastName,
                contact.Company,
                contact.Segment,
                PreferredChannel = contact.PreferredChannel.HasValue ? (int?)contact.PreferredChannel.Value : null,
                contact.PreferredLanguage,
                contact.Timezone,
                contact.DoNotContact,
                Addresses = addressesJson,
                CustomFields = customFieldsJson,
                ChannelConsent = consentJson,
                contact.CreatedAt,
                contact.UpdatedAt,
                contact.CreatedBy,
                contact.UpdatedBy,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM contacts WHERE tenant_id = @TenantId AND contact_id = @ContactId",
            new { TenantId = tenantId.Value, ContactId = contactId.Value });
    }

    private sealed record ContactRow(
        string contact_id,
        string tenant_id,
        string? first_name,
        string? last_name,
        string? company,
        string? segment,
        int? preferred_channel,
        string? preferred_language,
        string? timezone,
        bool do_not_contact,
        string addresses,
        string custom_fields,
        string channel_consent,
        DateTimeOffset created_at,
        DateTimeOffset? updated_at,
        string? created_by,
        string? updated_by)
    {
        public Contact ToContact()
        {
            var addressList = JsonSerializer.Deserialize(addresses, PostgresJson.Ctx.IReadOnlyListChannelAddress)
                              ?? (IReadOnlyList<ChannelAddress>)[];
            var customFieldDict = JsonSerializer.Deserialize(custom_fields, PostgresJson.Ctx.IReadOnlyDictionaryStringString)
                                  ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
            var consentDict = JsonSerializer.Deserialize(channel_consent, PostgresJson.Ctx.DictionaryInt32Boolean)
                              ?? new Dictionary<int, bool>();

            var contact = new Contact
            {
                ContactId = EntityId.From(contact_id),
                TenantId = new TenantId(tenant_id),
                FirstName = first_name,
                LastName = last_name,
                Company = company,
                Segment = segment,
                PreferredChannel = preferred_channel.HasValue ? (ChannelType?)preferred_channel.Value : null,
                PreferredLanguage = preferred_language,
                Timezone = timezone,
                DoNotContact = do_not_contact,
                CreatedAt = created_at,
                UpdatedAt = updated_at,
                CreatedBy = created_by,
                UpdatedBy = updated_by,
            };

            foreach (var addr in addressList)
                contact.AddAddress(addr);

            foreach (var kv in customFieldDict)
                contact.SetCustomField(kv.Key, kv.Value);

            foreach (var kv in consentDict)
                contact.SetConsent((ChannelType)kv.Key, kv.Value);

            return contact;
        }
    }
}
