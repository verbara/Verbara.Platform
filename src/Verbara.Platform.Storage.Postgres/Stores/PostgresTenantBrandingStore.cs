using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core.Branding;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantBrandingStore : ITenantBrandingStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantBrandingStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "tenant_id, display_name, logo_url, favicon_url, primary_color, secondary_color, " +
        "accent_color, locale, timezone, subdomain, support_email, support_url, " +
        "email_from_name, email_from_address, created_at, updated_at";

    public async ValueTask<TenantBranding?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM tenant_branding WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
            BrandingRow.Map, ct);

        return row?.ToModel();
    }

    public async ValueTask<TenantBranding?> GetBySubdomainAsync(string subdomain, CancellationToken ct = default)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM tenant_branding WHERE subdomain = @Subdomain",
            p => p.Add(new NpgsqlParameter("Subdomain", subdomain)),
            BrandingRow.Map, ct);

        return row?.ToModel();
    }

    public async ValueTask UpsertAsync(TenantBranding branding, CancellationToken ct = default)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO tenant_branding " +
            "(tenant_id, display_name, logo_url, favicon_url, primary_color, secondary_color, " +
            " accent_color, locale, timezone, subdomain, support_email, support_url, " +
            " email_from_name, email_from_address, created_at, updated_at) " +
            "VALUES (@TenantId, @DisplayName, @LogoUrl, @FaviconUrl, @PrimaryColor, @SecondaryColor, " +
            "        @AccentColor, @Locale, @Timezone, @Subdomain, @SupportEmail, @SupportUrl, " +
            "        @EmailFromName, @EmailFromAddress, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "  display_name       = EXCLUDED.display_name, " +
            "  logo_url           = EXCLUDED.logo_url, " +
            "  favicon_url        = EXCLUDED.favicon_url, " +
            "  primary_color      = EXCLUDED.primary_color, " +
            "  secondary_color    = EXCLUDED.secondary_color, " +
            "  accent_color       = EXCLUDED.accent_color, " +
            "  locale             = EXCLUDED.locale, " +
            "  timezone           = EXCLUDED.timezone, " +
            "  subdomain          = EXCLUDED.subdomain, " +
            "  support_email      = EXCLUDED.support_email, " +
            "  support_url        = EXCLUDED.support_url, " +
            "  email_from_name    = EXCLUDED.email_from_name, " +
            "  email_from_address = EXCLUDED.email_from_address, " +
            "  updated_at         = EXCLUDED.updated_at",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", branding.TenantId));
                p.Add(new NpgsqlParameter("DisplayName", NpgsqlDbType.Text) { Value = (object?)branding.DisplayName ?? DBNull.Value });
                p.Add(new NpgsqlParameter("LogoUrl", NpgsqlDbType.Text) { Value = (object?)branding.LogoUrl ?? DBNull.Value });
                p.Add(new NpgsqlParameter("FaviconUrl", NpgsqlDbType.Text) { Value = (object?)branding.FaviconUrl ?? DBNull.Value });
                p.Add(new NpgsqlParameter("PrimaryColor", NpgsqlDbType.Text) { Value = (object?)branding.PrimaryColor ?? DBNull.Value });
                p.Add(new NpgsqlParameter("SecondaryColor", NpgsqlDbType.Text) { Value = (object?)branding.SecondaryColor ?? DBNull.Value });
                p.Add(new NpgsqlParameter("AccentColor", NpgsqlDbType.Text) { Value = (object?)branding.AccentColor ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Locale", NpgsqlDbType.Text) { Value = (object?)branding.Locale ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Timezone", NpgsqlDbType.Text) { Value = (object?)branding.Timezone ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Subdomain", NpgsqlDbType.Text) { Value = (object?)branding.Subdomain ?? DBNull.Value });
                p.Add(new NpgsqlParameter("SupportEmail", NpgsqlDbType.Text) { Value = (object?)branding.SupportEmail ?? DBNull.Value });
                p.Add(new NpgsqlParameter("SupportUrl", NpgsqlDbType.Text) { Value = (object?)branding.SupportUrl ?? DBNull.Value });
                p.Add(new NpgsqlParameter("EmailFromName", NpgsqlDbType.Text) { Value = (object?)branding.EmailFromName ?? DBNull.Value });
                p.Add(new NpgsqlParameter("EmailFromAddress", NpgsqlDbType.Text) { Value = (object?)branding.EmailFromAddress ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedAt", branding.CreatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("UpdatedAt", branding.UpdatedAt.UtcDateTime));
            },
            ct);
    }

    private sealed class BrandingRow
    {
        public string tenant_id { get; init; } = null!;
        public string? display_name { get; init; }
        public string? logo_url { get; init; }
        public string? favicon_url { get; init; }
        public string? primary_color { get; init; }
        public string? secondary_color { get; init; }
        public string? accent_color { get; init; }
        public string? locale { get; init; }
        public string? timezone { get; init; }
        public string? subdomain { get; init; }
        public string? support_email { get; init; }
        public string? support_url { get; init; }
        public string? email_from_name { get; init; }
        public string? email_from_address { get; init; }
        public DateTime created_at { get; init; }
        public DateTime updated_at { get; init; }

        public static BrandingRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            display_name = r.GetStringOrNull("display_name"),
            logo_url = r.GetStringOrNull("logo_url"),
            favicon_url = r.GetStringOrNull("favicon_url"),
            primary_color = r.GetStringOrNull("primary_color"),
            secondary_color = r.GetStringOrNull("secondary_color"),
            accent_color = r.GetStringOrNull("accent_color"),
            locale = r.GetStringOrNull("locale"),
            timezone = r.GetStringOrNull("timezone"),
            subdomain = r.GetStringOrNull("subdomain"),
            support_email = r.GetStringOrNull("support_email"),
            support_url = r.GetStringOrNull("support_url"),
            email_from_name = r.GetStringOrNull("email_from_name"),
            email_from_address = r.GetStringOrNull("email_from_address"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTime("updated_at"),
        };

        public TenantBranding ToModel() => new()
        {
            TenantId         = tenant_id,
            DisplayName      = display_name,
            LogoUrl          = logo_url,
            FaviconUrl       = favicon_url,
            PrimaryColor     = primary_color,
            SecondaryColor   = secondary_color,
            AccentColor      = accent_color,
            Locale           = locale,
            Timezone         = timezone,
            Subdomain        = subdomain,
            SupportEmail     = support_email,
            SupportUrl       = support_url,
            EmailFromName    = email_from_name,
            EmailFromAddress = email_from_address,
            CreatedAt        = new DateTimeOffset(created_at, TimeSpan.Zero),
            UpdatedAt        = new DateTimeOffset(updated_at, TimeSpan.Zero),
        };
    }
}
