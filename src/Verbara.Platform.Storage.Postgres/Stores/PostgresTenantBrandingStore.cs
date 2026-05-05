using Dapper;
using Npgsql;
using Verbara.Platform.Core.Branding;

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
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<BrandingRow>(
            $"SELECT {SelectColumns} FROM tenant_branding WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });

        return row?.ToModel();
    }

    public async ValueTask<TenantBranding?> GetBySubdomainAsync(string subdomain, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<BrandingRow>(
            $"SELECT {SelectColumns} FROM tenant_branding WHERE subdomain = @Subdomain",
            new { Subdomain = subdomain });

        return row?.ToModel();
    }

    public async ValueTask UpsertAsync(TenantBranding branding, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
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
            new
            {
                TenantId         = branding.TenantId,
                DisplayName      = branding.DisplayName,
                LogoUrl          = branding.LogoUrl,
                FaviconUrl       = branding.FaviconUrl,
                PrimaryColor     = branding.PrimaryColor,
                SecondaryColor   = branding.SecondaryColor,
                AccentColor      = branding.AccentColor,
                Locale           = branding.Locale,
                Timezone         = branding.Timezone,
                Subdomain        = branding.Subdomain,
                SupportEmail     = branding.SupportEmail,
                SupportUrl       = branding.SupportUrl,
                EmailFromName    = branding.EmailFromName,
                EmailFromAddress = branding.EmailFromAddress,
                CreatedAt        = branding.CreatedAt.UtcDateTime,
                UpdatedAt        = branding.UpdatedAt.UtcDateTime,
            });
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
