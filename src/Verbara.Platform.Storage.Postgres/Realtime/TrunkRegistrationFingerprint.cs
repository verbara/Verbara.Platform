using System.Globalization;
using Npgsql;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Realtime;

/// <summary>
/// #385 — a fingerprint of the Asterisk Realtime <c>ps_registrations</c> table (the rows Asterisk's
/// <c>res_pjsip_outbound_registration</c> reads), so a convergence pass can tell whether the table changed
/// since the last module reload Asterisk accepted. Covers the columns the Realtime sync writes: id,
/// server_uri, client_uri, transport and outbound_auth.
/// </summary>
public static class TrunkRegistrationFingerprint
{
    internal const string Sql = """
        SELECT count(*) AS row_count,
               coalesce(md5(string_agg(
                   id || '|' || coalesce(server_uri, '') || '|' || coalesce(client_uri, '') || '|'
                      || coalesce(transport, '') || '|' || coalesce(outbound_auth, ''),
                   ',' ORDER BY id)), '') AS digest
        FROM ps_registrations
        """;

    /// <summary>
    /// Reads the fingerprint as <c>{row count}:{md5 of the ordered rows}</c> (<c>0:</c> for an empty table).
    /// </summary>
    public static async Task<string> ReadAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return await dataSource.QuerySingleAsync(
            Sql,
            static _ => { },
            static r => string.Create(CultureInfo.InvariantCulture,
                $"{r.GetInt64("row_count")}:{r.GetString("digest")}"),
            ct).ConfigureAwait(false);
    }
}
