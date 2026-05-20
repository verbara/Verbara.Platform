using System.Text.Json;
using Npgsql;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

public sealed class PostgresRateCardStore : IRateCardStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresRateCardStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task SaveAsync(RateCard rateCard, CancellationToken ct)
    {
        var ratesJson = JsonSerializer.Serialize(rateCard.Rates, PostgresJson.Ctx.IReadOnlyListRateEntry);

        await _dataSource.ExecuteAsync(
            "INSERT INTO rate_cards (rate_card_id, tenant_id, name, currency, effective_from, effective_to, rates, is_default) " +
            "VALUES (@RateCardId, @TenantId, @Name, @Currency, @EffectiveFrom, @EffectiveTo, @Rates::jsonb, @IsDefault) " +
            "ON CONFLICT (rate_card_id) DO UPDATE SET " +
            "name = EXCLUDED.name, currency = EXCLUDED.currency, effective_from = EXCLUDED.effective_from, " +
            "effective_to = EXCLUDED.effective_to, rates = EXCLUDED.rates, is_default = EXCLUDED.is_default",
            p =>
            {
                p.Add(new NpgsqlParameter("RateCardId", rateCard.RateCardId.Value));
                p.Add(new NpgsqlParameter("TenantId", rateCard.TenantId.Value));
                p.Add(new NpgsqlParameter("Name", rateCard.Name));
                p.Add(new NpgsqlParameter("Currency", rateCard.Currency));
                p.Add(new NpgsqlParameter("EffectiveFrom", rateCard.EffectiveFrom));
                p.Add(new NpgsqlParameter("EffectiveTo", (object?)rateCard.EffectiveTo ?? DBNull.Value));
                p.Add(new NpgsqlParameter("Rates", ratesJson));
                p.Add(new NpgsqlParameter("IsDefault", rateCard.IsDefault));
            },
            ct);
    }

    public async Task<RateCard?> GetByIdAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT rate_card_id, tenant_id, name, currency, effective_from, effective_to, rates, is_default " +
            "FROM rate_cards WHERE tenant_id = @TenantId AND rate_card_id = @RateCardId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("RateCardId", rateCardId.Value)); },
            RateCardRow.Map, ct);
        return row?.ToRateCard();
    }

    public async Task<RateCard?> GetActiveAsync(TenantId tenantId, DateTimeOffset asOf, CancellationToken ct)
    {
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            "SELECT rate_card_id, tenant_id, name, currency, effective_from, effective_to, rates, is_default " +
            "FROM rate_cards WHERE tenant_id = @TenantId AND effective_from <= @AsOf " +
            "AND (effective_to IS NULL OR effective_to > @AsOf) " +
            "ORDER BY effective_from DESC LIMIT 1",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("AsOf", asOf)); },
            RateCardRow.Map, ct);
        return row?.ToRateCard();
    }

    public async Task<IReadOnlyList<RateCard>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT rate_card_id, tenant_id, name, currency, effective_from, effective_to, rates, is_default " +
            "FROM rate_cards WHERE tenant_id = @TenantId ORDER BY effective_from DESC",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            RateCardRow.Map, ct);
        return rows.Select(r => r.ToRateCard()).ToList();
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM rate_cards WHERE tenant_id = @TenantId AND rate_card_id = @RateCardId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("RateCardId", rateCardId.Value)); },
            ct);
    }

    private sealed class RateCardRow
    {
        public string rate_card_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public string currency { get; init; } = null!;
        public DateTime effective_from { get; init; }
        public DateTime? effective_to { get; init; }
        public string rates { get; init; } = null!;
        public bool is_default { get; init; }

        public static RateCardRow Map(NpgsqlDataReader r) => new()
        {
            rate_card_id = r.GetString("rate_card_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            currency = r.GetString("currency"),
            effective_from = r.GetDateTime("effective_from"),
            effective_to = r.GetDateTimeOrNull("effective_to"),
            rates = r.GetString("rates"),
            is_default = r.GetBoolean("is_default"),
        };

        public RateCard ToRateCard() => new()
        {
            RateCardId = EntityId.From(rate_card_id),
            TenantId = new TenantId(tenant_id),
            Name = name,
            Currency = currency,
            EffectiveFrom = effective_from,
            EffectiveTo = effective_to,
            Rates = JsonSerializer.Deserialize(rates, PostgresJson.Ctx.IReadOnlyListRateEntry) ?? [],
            IsDefault = is_default,
        };
    }
}
