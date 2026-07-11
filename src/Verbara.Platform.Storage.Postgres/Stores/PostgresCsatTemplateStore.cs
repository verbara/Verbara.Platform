using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Npgsql-facade store over the <c>csat_templates</c> table (csat-runner Phase E,
/// migration 016). Keyed <c>(tenant_id, template_id)</c>; channel constrained to
/// <c>voice</c>/<c>email</c>/<c>sms</c> by <c>chk_csat_templates_channel</c>. No Dapper
/// (Platform/ADR-0022) — every nullable param that can be <see cref="DBNull"/> sets an
/// explicit <see cref="NpgsqlDbType"/> or Postgres throws <c>42P08</c>.
/// </summary>
internal sealed class PostgresCsatTemplateStore : ICsatTemplateStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCsatTemplateStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "SELECT tenant_id, template_id, channel, locale, subject, body, is_default, created_at, updated_at ";

    public async Task<IReadOnlyList<CsatTemplateEntry>> GetAllAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            SelectColumns +
            "FROM csat_templates WHERE tenant_id = @TenantId " +
            "ORDER BY channel, locale, template_id",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            TemplateRow.Map, ct).ConfigureAwait(false);
        return rows.Select(r => r.ToEntry()).ToList();
    }

    public async Task<CsatTemplateEntry?> GetByIdAsync(TenantId tenantId, EntityId templateId, CancellationToken ct)
    {
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            SelectColumns +
            "FROM csat_templates WHERE tenant_id = @TenantId AND template_id = @TemplateId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("TemplateId", templateId.Value));
            },
            TemplateRow.Map, ct).ConfigureAwait(false);
        return row?.ToEntry();
    }

    public async Task<IReadOnlyList<CsatTemplateEntry>> GetByChannelAndLocaleAsync(
        TenantId tenantId, string channel, string locale, CancellationToken ct)
    {
        // Served by idx_csat_templates_lookup (tenant_id, channel, locale).
        var rows = await _dataSource.QueryListAsync(
            SelectColumns +
            "FROM csat_templates " +
            "WHERE tenant_id = @TenantId AND channel = @Channel AND locale = @Locale " +
            "ORDER BY is_default DESC, template_id",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Channel", channel));
                p.Add(new NpgsqlParameter("Locale", locale));
            },
            TemplateRow.Map, ct).ConfigureAwait(false);
        return rows.Select(r => r.ToEntry()).ToList();
    }

    public async Task<IReadOnlyList<CsatTemplateEntry>> GetDefaultsByChannelAsync(
        TenantId tenantId, string channel, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            SelectColumns +
            "FROM csat_templates " +
            "WHERE tenant_id = @TenantId AND channel = @Channel AND is_default = true " +
            "ORDER BY locale, template_id",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Channel", channel));
            },
            TemplateRow.Map, ct).ConfigureAwait(false);
        return rows.Select(r => r.ToEntry()).ToList();
    }

    public async Task SaveAsync(CsatTemplateEntry entry, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO csat_templates " +
            "(tenant_id, template_id, channel, locale, subject, body, is_default, created_at, updated_at) " +
            "VALUES (@TenantId, @TemplateId, @Channel, @Locale, @Subject, @Body, @IsDefault, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT (tenant_id, template_id) DO UPDATE SET " +
            "channel = EXCLUDED.channel, locale = EXCLUDED.locale, subject = EXCLUDED.subject, " +
            "body = EXCLUDED.body, is_default = EXCLUDED.is_default, updated_at = now()",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", entry.TenantId.Value));
                p.Add(new NpgsqlParameter("TemplateId", entry.TemplateId.Value));
                p.Add(new NpgsqlParameter("Channel", entry.Channel));
                p.Add(new NpgsqlParameter("Locale", entry.Locale));
                // Nullable params get an explicit NpgsqlDbType (ADR-0022 idiom).
                p.Add(new NpgsqlParameter("Subject", NpgsqlDbType.Text) { Value = (object?)entry.Subject ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Body", entry.Body));
                p.Add(new NpgsqlParameter("IsDefault", NpgsqlDbType.Boolean) { Value = entry.IsDefault });
                p.Add(new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = entry.CreatedAt });
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = (object?)entry.UpdatedAt ?? DBNull.Value });
            },
            ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId templateId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM csat_templates WHERE tenant_id = @TenantId AND template_id = @TemplateId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("TemplateId", templateId.Value));
            },
            ct).ConfigureAwait(false);
    }

    private sealed class TemplateRow
    {
        public string tenant_id { get; init; } = null!;
        public string template_id { get; init; } = null!;
        public string channel { get; init; } = null!;
        public string locale { get; init; } = null!;
        public string? subject { get; init; }
        public string body { get; init; } = null!;
        public bool is_default { get; init; }
        public DateTimeOffset created_at { get; init; }
        public DateTimeOffset? updated_at { get; init; }

        public static TemplateRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            template_id = r.GetString("template_id"),
            channel = r.GetString("channel"),
            locale = r.GetString("locale"),
            subject = r.GetStringOrNull("subject"),
            body = r.GetString("body"),
            is_default = r.GetBoolean("is_default"),
            created_at = r.GetDateTimeOffset("created_at"),
            updated_at = r.GetDateTimeOffsetOrNull("updated_at"),
        };

        public CsatTemplateEntry ToEntry() => new()
        {
            TenantId = new TenantId(tenant_id),
            TemplateId = EntityId.From(template_id),
            Channel = channel,
            Locale = locale,
            Subject = subject,
            Body = body,
            IsDefault = is_default,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
        };
    }
}
