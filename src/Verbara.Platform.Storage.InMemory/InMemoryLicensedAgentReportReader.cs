using Verbara.Platform.Queues.Licensing;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// The in-memory read side of the licensed-agent reports (licensed-agent-reporting). The in-memory host runs
/// no daily close (it needs Postgres, design D7), so it reports the ledger rows and chain heads it holds and
/// no daily rows; the export applies the same <see cref="LicensedAgentExportSpan"/> rule as Postgres.
/// </summary>
public sealed class InMemoryLicensedAgentReportReader : ILicensedAgentReportReader
{
    private readonly InMemoryLicenseAgentLedger _ledger;
    private readonly ITenantStore _tenants;

    public InMemoryLicensedAgentReportReader(InMemoryLicenseAgentLedger ledger, ITenantStore tenants)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(tenants);
        _ledger = ledger;
        _tenants = tenants;
    }

    public async Task<LicensedAgentPeaksData> ReadPeaksAsync(
        DateOnly firstDay, DateOnly lastDay, string fallbackDayZone, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDayZone);
        var (heads, _) = _ledger.Snapshot();
        var tenants = await TenantsAsync(heads.Select(h => h.TenantId), ct).ConfigureAwait(false);
        return new LicensedAgentPeaksData(fallbackDayZone, [], tenants, heads);
    }

    public async Task<LicensedAgentExportData> ReadExportAsync(
        DateOnly firstDay, DateOnly lastDay, string fallbackDayZone, CancellationToken ct)
    {
        var zone = LicenseAgentDayZone.Resolve(fallbackDayZone);
        var (start, end) = LicensedAgentExportSpan.Bounds(firstDay, lastDay, zone);
        var (heads, events) = _ledger.Snapshot();

        var selected = new List<LicenseAgentEvent>();
        foreach (var head in heads)
        {
            var chain = events.Where(e => e.TenantId == head.TenantId).ToList();
            if (LicensedAgentExportSpan.Range(chain, [], head.HeadSequence, start, end, firstDay, lastDay) is { } span)
                selected.AddRange(chain.Where(e => e.Sequence >= span.First && e.Sequence <= span.Last));
        }

        var tenants = await TenantsAsync(selected.Select(e => e.TenantId).Concat(heads.Select(h => h.TenantId)), ct)
            .ConfigureAwait(false);
        return new LicensedAgentExportData(fallbackDayZone, selected, [], heads, tenants);
    }

    // The named tenants and every parent above them (design D10), as the Postgres reader reads them.
    private async Task<Dictionary<string, LicensedAgentTenantRef>> TenantsAsync(IEnumerable<string?> tenantIds, CancellationToken ct)
    {
        var tenants = new Dictionary<string, LicensedAgentTenantRef>(StringComparer.Ordinal);
        var pending = new Queue<string>(tenantIds.OfType<string>().Distinct(StringComparer.Ordinal));
        while (pending.TryDequeue(out var tenantId))
        {
            if (tenants.ContainsKey(tenantId))
                continue;
            var tenant = await _tenants.GetAsync(tenantId, ct).ConfigureAwait(false);
            if (tenant is null)
                continue;
            tenants[tenantId] = new LicensedAgentTenantRef(tenant.Name, tenant.Type.ToString(), tenant.ParentTenantId);
            if (tenant.ParentTenantId is { } parent)
                pending.Enqueue(parent);
        }

        return tenants;
    }
}
