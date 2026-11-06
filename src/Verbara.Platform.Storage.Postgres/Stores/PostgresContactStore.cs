using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresContactStore : IContactStore
{
    private const string ReturnedColumns =
        "contact_id, tenant_id, first_name, last_name, company, segment, preferred_channel, " +
        "preferred_language, timezone, do_not_contact, addresses, custom_fields, channel_consent, " +
        "created_at, updated_at, created_by, updated_by";

    private const string FindByAddressSql =
        "SELECT " + ReturnedColumns + " " +
        "FROM contacts WHERE tenant_id = @TenantId " +
        "  AND EXISTS (SELECT 1 FROM jsonb_array_elements(addresses) AS a " +
        "              WHERE a->>'channel' IN (@ChannelName, @ChannelOrdinal) AND a->>'address' = @Address)";

    // contacts-writes-follow-their-permissions (design D10): ChannelAddressMatch.KeyOf in SQL, on each element of the
    // channel in either of its forms (the enum name, or the ordinal written before 2.13.0). A phone-like channel compares
    // VoiceNumber.Canonical; the blank class is written out as the four ASCII characters C# skips, never \s (which
    // follows the database's locale), and a stored address with no digit matches nothing, so it never reduces to a lone
    // +. Any other channel compares the address with the same four characters trimmed from both ends. The order puts
    // every restricted contact ahead of every unrestricted one, so the bound drops only rows that cannot change a
    // refusal; channel_consent keys are the channel's ordinal, the only key form SaveAsync writes.
    private const string FindAllByChannelAddressSelect =
        "SELECT " + ReturnedColumns + " " +
        "FROM contacts WHERE tenant_id = @TenantId " +
        "  AND EXISTS (SELECT 1 FROM jsonb_array_elements(addresses) AS a " +
        "              WHERE a->>'channel' IN (@ChannelName, @ChannelOrdinal) AND ";

    private const string PhoneLikeAddressMatches =
        "regexp_replace(a->>'address', '[^0-9]', '', 'g') <> '' " +
        "AND (CASE WHEN a->>'address' ~ '^[ \\t\\r\\n]*\\+' THEN '+' ELSE '' END) " +
        "    || regexp_replace(a->>'address', '[^0-9]', '', 'g') = @Key";

    private const string ExactAddressMatches =
        "btrim(a->>'address', ' ' || chr(9) || chr(13) || chr(10)) = @Key";

    private const string FindAllByChannelAddressOrder =
        ") ORDER BY do_not_contact DESC, COALESCE(channel_consent->@ChannelOrdinal = 'false'::jsonb, false) DESC, contact_id " +
        "LIMIT @Limit";

    private const string FindAllByPhoneLikeAddressSql =
        FindAllByChannelAddressSelect + PhoneLikeAddressMatches + FindAllByChannelAddressOrder;

    private const string FindAllByExactAddressSql =
        FindAllByChannelAddressSelect + ExactAddressMatches + FindAllByChannelAddressOrder;

    private const string SearchPredicate =
        "(first_name ILIKE @Term OR last_name ILIKE @Term OR company ILIKE @Term " +
        "OR custom_fields->>'" + Contact.WebChatEmailField + "' ILIKE @Term)";

    // Shared with PostgresContactAuditWriter, which runs the same statement inside its transaction.
    internal const string SaveSql =
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
        "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by";

    internal const string DeleteSql = "DELETE FROM contacts WHERE tenant_id = @TenantId AND contact_id = @ContactId";

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
        // addresses[].channel has two on-disk forms: the enum name ("WhatsApp"), written since
        // PostgresJsonContext gained UseStringEnumConverter (2.13.0), and the ordinal (1) in rows
        // written before. Compare the text form against both instead of casting to int, which
        // throws 22P02 on a name. No migration: both forms stay readable by ContactRow.ToContact.
        var rows = await _dataSource.QueryListAsync(
            FindByAddressSql,
            p => AddFindParameters(p, tenantId, address),
            ContactRow.Map, ct);
        return rows.Select(r => r.ToContact()).FirstOrDefault();
    }

    public async Task<IReadOnlyList<Contact>> FindAllByChannelAddressAsync(
        TenantId tenantId, ChannelType channel, string address, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        var key = ChannelAddressMatch.KeyOf(channel, address);
        if (key.Length == 0)
            return [];

        var rows = await _dataSource.QueryListAsync(
            ChannelAddressMatch.IsPhoneLike(channel) ? FindAllByPhoneLikeAddressSql : FindAllByExactAddressSql,
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("ChannelName", NpgsqlDbType.Text) { Value = channel.ToString() });
                p.Add(new NpgsqlParameter("ChannelOrdinal", NpgsqlDbType.Text)
                {
                    Value = ((int)channel).ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
                p.Add(new NpgsqlParameter("Key", NpgsqlDbType.Text) { Value = key });
                p.Add(new NpgsqlParameter("Limit", NpgsqlDbType.Integer) { Value = Math.Max(0, limit) });
            },
            ContactRow.Map, ct);
        return [.. rows.Select(r => r.ToContact())];
    }

    private static void AddFindParameters(NpgsqlParameterCollection p, TenantId tenantId, ChannelAddress address)
    {
        p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
        p.Add(new NpgsqlParameter("ChannelName", NpgsqlDbType.Text) { Value = address.Channel.ToString() });
        p.Add(new NpgsqlParameter("ChannelOrdinal", NpgsqlDbType.Text)
        {
            Value = ((int)address.Channel).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        p.Add(new NpgsqlParameter("Address", NpgsqlDbType.Text) { Value = address.Address });
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
                "SELECT COUNT(*) FROM contacts WHERE tenant_id = @TenantId AND " + SearchPredicate,
                p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Term", term)); }, ct) ?? 0L);
            rows = await _dataSource.QueryListAsync(
                "SELECT contact_id, tenant_id, first_name, last_name, company, segment, preferred_channel, " +
                "preferred_language, timezone, do_not_contact, addresses, custom_fields, channel_consent, " +
                "created_at, updated_at, created_by, updated_by " +
                "FROM contacts WHERE tenant_id = @TenantId AND " + SearchPredicate + " " +
                "ORDER BY created_at LIMIT @Limit OFFSET @Offset",
                p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Term", term)); p.Add(new NpgsqlParameter("Limit", query.PageSize)); p.Add(new NpgsqlParameter("Offset", query.Offset)); },
                ContactRow.Map, ct);
        }

        var items = rows.Select(r => r.ToContact()).ToList();
        return new PagedResult<Contact>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Contact contact, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(SaveSql, p => AddSaveParameters(p, contact), ct);
    }

    internal static void AddSaveParameters(NpgsqlParameterCollection p, Contact contact)
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
    }

    public async Task<Contact?> AddIdentityAsync(
        TenantId tenantId,
        EntityId contactId,
        IReadOnlyList<ChannelAddress> addresses,
        string? firstNameIfUnnamed,
        DateTimeOffset updatedAt,
        string updatedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var addressesJson = JsonSerializer.Serialize(addresses, PostgresJson.Ctx.IReadOnlyListChannelAddress);
        // The ordinal of each new address's channel, by position: a stored address may carry the ordinal form (rows
        // written before 2.13.0, see FindByAddressAsync), and must still count as already present.
        var ordinals = addresses
            .Select(a => ((int)a.Channel).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        // An UPDATE, never an upsert: a contact erased since the caller read it matches no row and stays erased.
        var rows = await _dataSource.QueryListAsync(
            "UPDATE contacts SET " +
            "  addresses = contacts.addresses || COALESCE((" +
            "    SELECT jsonb_agg(n.value ORDER BY n.idx) " +
            "    FROM jsonb_array_elements(@Addresses::jsonb) WITH ORDINALITY AS n(value, idx) " +
            "    WHERE NOT EXISTS (SELECT 1 FROM jsonb_array_elements(contacts.addresses) AS e " +
            "                      WHERE e->>'address' = n.value->>'address' " +
            "                        AND e->>'channel' IN (n.value->>'channel', (@Ordinals)[n.idx]))" +
            "  ), '[]'::jsonb), " +
            "  first_name = CASE WHEN contacts.first_name IS NULL AND contacts.last_name IS NULL " +
            "                    THEN COALESCE(@FirstName, contacts.first_name) ELSE contacts.first_name END, " +
            "  updated_at = @UpdatedAt, updated_by = @UpdatedBy " +
            "WHERE tenant_id = @TenantId AND contact_id = @ContactId " +
            "RETURNING contact_id, tenant_id, first_name, last_name, company, segment, preferred_channel, " +
            "preferred_language, timezone, do_not_contact, addresses, custom_fields, channel_consent, " +
            "created_at, updated_at, created_by, updated_by",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("ContactId", NpgsqlDbType.Text) { Value = contactId.Value });
                p.Add(new NpgsqlParameter("Addresses", addressesJson));
                p.Add(new NpgsqlParameter("Ordinals", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ordinals });
                p.Add(new NpgsqlParameter("FirstName", NpgsqlDbType.Text) { Value = (object?)firstNameIfUnnamed ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = updatedAt.ToUniversalTime() });
                p.Add(new NpgsqlParameter("UpdatedBy", NpgsqlDbType.Text) { Value = updatedBy });
            },
            ContactRow.Map, ct);
        return rows.Count == 0 ? null : rows[0].ToContact();
    }

    public async Task<Contact> FindOrInsertByAddressAsync(TenantId tenantId, ChannelAddress key, Contact newContact, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(newContact);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        // The find must see a row committed while this transaction waited for the lock, and only READ COMMITTED gives a
        // statement issued after the wait its own snapshot. Under a stricter level the second caller would insert too.
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        await using (var serialize = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@TenantId || '|' || @Key, 0))", conn, tx))
        {
            serialize.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
            serialize.Parameters.Add(new NpgsqlParameter("Key", NpgsqlDbType.Text) { Value = key.Address });
            await serialize.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        Contact? held = null;
        await using (var find = new NpgsqlCommand(FindByAddressSql, conn, tx))
        {
            AddFindParameters(find.Parameters, tenantId, key);
            await using var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                held = ContactRow.Map(reader).ToContact();
        }

        if (held is null)
        {
            await using var insert = new NpgsqlCommand(SaveSql, conn, tx);
            AddSaveParameters(insert.Parameters, newContact);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return held ?? newContact;
    }

    public async Task<Contact?> AttachWebChatSessionAsync(
        TenantId tenantId,
        EntityId contactId,
        ChannelAddress sessionAddress,
        string? firstNameIfUnnamed,
        DateTimeOffset updatedAt,
        string updatedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessionAddress);
        var addressJson = JsonSerializer.Serialize<IReadOnlyList<ChannelAddress>>(
            [sessionAddress], PostgresJson.Ctx.IReadOnlyListChannelAddress);

        // One statement, so concurrent attaches of one contact queue on its row and none loses an address. The array is
        // in the order the addresses were added, so "newest" is position. "newer" counts the session addresses from each
        // element to the end of the array. Only session addresses count, on either on-disk form of the channel (see
        // FindByAddressAsync); a key or any other address is kept. An UPDATE, never an upsert: an erased contact stays erased.
        var rows = await _dataSource.QueryListAsync(
            "UPDATE contacts SET " +
            "  addresses = (" +
            "    SELECT COALESCE(jsonb_agg(kept.value ORDER BY kept.idx), '[]'::jsonb) FROM (" +
            "      SELECT a.value, a.idx, a.is_session, " +
            "             count(*) FILTER (WHERE a.is_session) OVER (ORDER BY a.idx ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) AS newer " +
            "      FROM (" +
            "        SELECT e.value, e.idx, " +
            "               (e.value->>'channel' IN (@ChannelName, @ChannelOrdinal) AND e.value->>'address' ~ '^webchat-[0-9a-f]{32}$') AS is_session " +
            "        FROM jsonb_array_elements(" +
            "          contacts.addresses || CASE WHEN EXISTS (" +
            "            SELECT 1 FROM jsonb_array_elements(contacts.addresses) AS h " +
            "            WHERE h->>'address' = @Address AND h->>'channel' IN (@ChannelName, @ChannelOrdinal)) " +
            "          THEN '[]'::jsonb ELSE @AddressJson::jsonb END" +
            "        ) WITH ORDINALITY AS e(value, idx)" +
            "      ) AS a" +
            "    ) AS kept WHERE NOT kept.is_session OR kept.newer <= @Keep" +
            "  ), " +
            "  first_name = CASE WHEN contacts.first_name IS NULL AND contacts.last_name IS NULL " +
            "                    THEN COALESCE(@FirstName, contacts.first_name) ELSE contacts.first_name END, " +
            "  updated_at = @UpdatedAt, updated_by = @UpdatedBy " +
            "WHERE tenant_id = @TenantId AND contact_id = @ContactId " +
            "RETURNING " + ReturnedColumns,
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("ContactId", NpgsqlDbType.Text) { Value = contactId.Value });
                p.Add(new NpgsqlParameter("ChannelName", NpgsqlDbType.Text) { Value = sessionAddress.Channel.ToString() });
                p.Add(new NpgsqlParameter("ChannelOrdinal", NpgsqlDbType.Text)
                {
                    Value = ((int)sessionAddress.Channel).ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
                p.Add(new NpgsqlParameter("Address", NpgsqlDbType.Text) { Value = sessionAddress.Address });
                p.Add(new NpgsqlParameter("AddressJson", NpgsqlDbType.Text) { Value = addressJson });
                p.Add(new NpgsqlParameter("Keep", NpgsqlDbType.Integer) { Value = Contact.MaxWebChatSessionAddresses });
                p.Add(new NpgsqlParameter("FirstName", NpgsqlDbType.Text) { Value = (object?)firstNameIfUnnamed ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = updatedAt.ToUniversalTime() });
                p.Add(new NpgsqlParameter("UpdatedBy", NpgsqlDbType.Text) { Value = updatedBy });
            },
            ContactRow.Map, ct);
        return rows.Count == 0 ? null : rows[0].ToContact();
    }

    public async Task<Contact?> SetCustomFieldIfAbsentAsync(
        TenantId tenantId,
        EntityId contactId,
        string key,
        string value,
        DateTimeOffset updatedAt,
        string updatedBy,
        CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "UPDATE contacts SET custom_fields = custom_fields || jsonb_build_object(@Key, @Value), " +
            "updated_at = @UpdatedAt, updated_by = @UpdatedBy " +
            "WHERE tenant_id = @TenantId AND contact_id = @ContactId AND NOT (custom_fields ? @Key) " +
            "RETURNING " + ReturnedColumns,
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("ContactId", NpgsqlDbType.Text) { Value = contactId.Value });
                p.Add(new NpgsqlParameter("Key", NpgsqlDbType.Text) { Value = key });
                p.Add(new NpgsqlParameter("Value", NpgsqlDbType.Text) { Value = value });
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = updatedAt.ToUniversalTime() });
                p.Add(new NpgsqlParameter("UpdatedBy", NpgsqlDbType.Text) { Value = updatedBy });
            },
            ContactRow.Map, ct);
        return rows.Count == 0 ? null : rows[0].ToContact();
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(DeleteSql, p => AddDeleteParameters(p, tenantId, contactId), ct);
    }

    internal static void AddDeleteParameters(NpgsqlParameterCollection p, TenantId tenantId, EntityId contactId)
    {
        p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
        p.Add(new NpgsqlParameter("ContactId", NpgsqlDbType.Text) { Value = contactId.Value });
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
