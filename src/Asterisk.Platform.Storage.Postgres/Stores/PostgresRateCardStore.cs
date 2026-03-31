using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

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

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO rate_cards (rate_card_id, tenant_id, name, currency, effective_from, effective_to, rates, is_default) " +
            "VALUES (@RateCardId, @TenantId, @Name, @Currency, @EffectiveFrom, @EffectiveTo, @Rates::jsonb, @IsDefault) " +
            "ON CONFLICT (rate_card_id) DO UPDATE SET " +
            "name = EXCLUDED.name, currency = EXCLUDED.currency, effective_from = EXCLUDED.effective_from, " +
            "effective_to = EXCLUDED.effective_to, rates = EXCLUDED.rates, is_default = EXCLUDED.is_default",
            new
            {
                RateCardId = rateCard.RateCardId.Value,
                TenantId = rateCard.TenantId.Value,
                rateCard.Name,
                rateCard.Currency,
                rateCard.EffectiveFrom,
                EffectiveTo = rateCard.EffectiveTo,
                Rates = ratesJson,
                rateCard.IsDefault,
            });
    }

    public async Task<RateCard?> GetByIdAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RateCardRow?>(
            "SELECT rate_card_id, tenant_id, name, currency, effective_from, effective_to, rates, is_default " +
            "FROM rate_cards WHERE tenant_id = @TenantId AND rate_card_id = @RateCardId",
            new { TenantId = tenantId.Value, RateCardId = rateCardId.Value });

        return row?.ToRateCard();
    }

    public async Task<RateCard?> GetActiveAsync(TenantId tenantId, DateTimeOffset asOf, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RateCardRow?>(
            "SELECT rate_card_id, tenant_id, name, currency, effective_from, effective_to, rates, is_default " +
            "FROM rate_cards WHERE tenant_id = @TenantId AND effective_from <= @AsOf " +
            "AND (effective_to IS NULL OR effective_to > @AsOf) " +
            "ORDER BY effective_from DESC LIMIT 1",
            new { TenantId = tenantId.Value, AsOf = asOf });

        return row?.ToRateCard();
    }

    public async Task<IReadOnlyList<RateCard>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RateCardRow>(
            "SELECT rate_card_id, tenant_id, name, currency, effective_from, effective_to, rates, is_default " +
            "FROM rate_cards WHERE tenant_id = @TenantId ORDER BY effective_from DESC",
            new { TenantId = tenantId.Value });

        return rows.Select(r => r.ToRateCard()).ToList();
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM rate_cards WHERE tenant_id = @TenantId AND rate_card_id = @RateCardId",
            new { TenantId = tenantId.Value, RateCardId = rateCardId.Value });
    }

    private sealed record RateCardRow(
        string rate_card_id,
        string tenant_id,
        string name,
        string currency,
        DateTimeOffset effective_from,
        DateTimeOffset? effective_to,
        string rates,
        bool is_default)
    {
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
