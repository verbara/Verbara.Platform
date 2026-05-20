using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresContactStore : IContactStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresContactStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Contact?> GetByIdAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT contact_id, tenant_id, first_name, last_name, company, segment, preferred_channel, " +
            "preferred_language, timezone, do_not_contact, addresses, custom_fields, channel_consent, " +
            "created_at, updated_at, created_by, updated_by " +
            "FROM contacts WHERE tenant_id = @TenantId AND contact_id = @ContactId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("ContactId", contactId.Value)); },
            ContactRow.Map, ct);
        return row?.ToContact();
    }

    public async Task<Contact?> FindByAddressAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT contact_id, tenant_id, first_name, last_name, company, segment, preferred_channel, " +
            "preferred_language, timezone, do_not_contact, addresses, custom_fields, channel_consent, " +
            "created_at, updated_at, created_by, updated_by " +
            "FROM contacts WHERE tenant_id = @TenantId " +
            "  AND EXISTS (SELECT 1 FROM jsonb_array_elements(addresses) AS a " +
            "              WHERE (a->>'channel')::int = @Channel AND a->>'address' = @Address)",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Channel", (int)address.Channel)); p.Add(new NpgsqlParameter("Address", address.Address)); },
            ContactRow.Map, ct);
        return rows.Select(r => r.ToContact()).FirstOrDefault();
    }

    public async Task<PagedResult<Contact>> SearchAsync(TenantId tenantId, string? searchTerm, PagedQuery query, CancellationToken ct)
    {
        int total;
        List<ContactRow> rows;

        if (string.IsNullOrEmpty(searchTerm))
        {
            total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
                "SELECT COUNT(*) FROM contacts WHERE tenant_id = @TenantId",
                p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)), ct) ?? 0L);
            rows = await _dataSource.QueryListAsync(
                "SELECT contact_id, tenant_id, first_name, last_name, company, segment, preferred_channel, " +
                "preferred_language, timezone, do_not_contact, addresses, custom_fields, channel_consent, " +
                "created_at, updated_at, created_by, updated_by " +
                "FROM contacts WHERE tenant_id = @TenantId ORDER BY created_at LIMIT @Limit OFFSET @Offset",
                p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Limit", query.PageSize)); p.Add(new NpgsqlParameter("Offset", query.Offset)); },
                ContactRow.Map, ct);
        }
        else
        {
            var term = $"%{searchTerm}%";
            total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
                "SELECT COUNT(*) FROM contacts WHERE tenant_id = @TenantId AND (first_name ILIKE @Term OR last_name ILIKE @Term OR company ILIKE @Term)",
                p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Term", term)); }, ct) ?? 0L);
            rows = await _dataSource.QueryListAsync(
                "SELECT contact_id, tenant_id, first_name, last_name, company, segment, preferred_channel, " +
                "preferred_language, timezone, do_not_contact, addresses, custom_fields, channel_consent, " +
                "created_at, updated_at, created_by, updated_by " +
                "FROM contacts WHERE tenant_id = @TenantId AND (first_name ILIKE @Term OR last_name ILIKE @Term OR company ILIKE @Term) " +
                "ORDER BY created_at LIMIT @Limit OFFSET @Offset",
                p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Term", term)); p.Add(new NpgsqlParameter("Limit", query.PageSize)); p.Add(new NpgsqlParameter("Offset", query.Offset)); },
                ContactRow.Map, ct);
        }

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

        await _dataSource.ExecuteAsync(
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
            p =>
            {
                p.Add(new NpgsqlParameter("ContactId", contact.ContactId.Value));
                p.Add(new NpgsqlParameter("TenantId", contact.TenantId.Value));
                p.Add(new NpgsqlParameter("FirstName", NpgsqlDbType.Text) { Value = (object?)contact.FirstName ?? DBNull.Value });
                p.Add(new NpgsqlParameter("LastName", NpgsqlDbType.Text) { Value = (object?)contact.LastName ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Company", NpgsqlDbType.Text) { Value = (object?)contact.Company ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Segment", NpgsqlDbType.Text) { Value = (object?)contact.Segment ?? DBNull.Value });
                p.Add(new NpgsqlParameter("PreferredChannel", contact.PreferredChannel.HasValue ? (object)(int)contact.PreferredChannel.Value : DBNull.Value));
                p.Add(new NpgsqlParameter("PreferredLanguage", NpgsqlDbType.Text) { Value = (object?)contact.PreferredLanguage ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Timezone", NpgsqlDbType.Text) { Value = (object?)contact.Timezone ?? DBNull.Value });
                p.Add(new NpgsqlParameter("DoNotContact", contact.DoNotContact));
                p.Add(new NpgsqlParameter("Addresses", addressesJson));
                p.Add(new NpgsqlParameter("CustomFields", customFieldsJson));
                p.Add(new NpgsqlParameter("ChannelConsent", consentJson));
                p.Add(new NpgsqlParameter("CreatedAt", contact.CreatedAt));
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = (object?)contact.UpdatedAt ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedBy", NpgsqlDbType.Text) { Value = (object?)contact.CreatedBy ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UpdatedBy", NpgsqlDbType.Text) { Value = (object?)contact.UpdatedBy ?? DBNull.Value });
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM contacts WHERE tenant_id = @TenantId AND contact_id = @ContactId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("ContactId", contactId.Value)); },
            ct);
    }

    private sealed class ContactRow
    {
        public string contact_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string? first_name { get; init; }
        public string? last_name { get; init; }
        public string? company { get; init; }
        public string? segment { get; init; }
        public int? preferred_channel { get; init; }
        public string? preferred_language { get; init; }
        public string? timezone { get; init; }
        public bool do_not_contact { get; init; }
        public string addresses { get; init; } = null!;
        public string custom_fields { get; init; } = null!;
        public string channel_consent { get; init; } = null!;
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }
        public string? created_by { get; init; }
        public string? updated_by { get; init; }

        public static ContactRow Map(NpgsqlDataReader r) => new()
        {
            contact_id = r.GetString("contact_id"),
            tenant_id = r.GetString("tenant_id"),
            first_name = r.GetStringOrNull("first_name"),
            last_name = r.GetStringOrNull("last_name"),
            company = r.GetStringOrNull("company"),
            segment = r.GetStringOrNull("segment"),
            preferred_channel = r.GetInt32OrNull("preferred_channel"),
            preferred_language = r.GetStringOrNull("preferred_language"),
            timezone = r.GetStringOrNull("timezone"),
            do_not_contact = r.GetBoolean("do_not_contact"),
            addresses = r.GetString("addresses"),
            custom_fields = r.GetString("custom_fields"),
            channel_consent = r.GetString("channel_consent"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
            created_by = r.GetStringOrNull("created_by"),
            updated_by = r.GetStringOrNull("updated_by"),
        };

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
