using System.Xml.Linq;
using Dapper;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Verbara.Platform.Identity.DataProtection;

internal static partial class DapperXmlRepositoryLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "[DPK] Skipping malformed row id={Id} friendly={FriendlyName}: {Reason}")]
    public static partial void MalformedRow(ILogger logger, long id, string friendlyName, string reason);
}

/// <summary>
/// AOT-safe Dapper implementation of ASP.NET Core DataProtection's
/// <see cref="IXmlRepository"/>. Reads + writes the same
/// <c>data_protection_keys</c> table that the legacy
/// <c>EntityFrameworkCoreXmlRepository</c> consumed (migrations V018 + V022),
/// so the on-disk schema is unchanged and existing keyrings stay valid across
/// the Phase B cutover.
/// </summary>
/// <remarks>
/// <para>
/// Persistence semantics mirror the stock EF-Core repository: every
/// <see cref="StoreElement"/> call inserts a new row carrying
/// (FriendlyName, Xml); <see cref="GetAllElements"/> returns every row
/// (revoked entries included — ASP.NET Core needs to see revoked keys to
/// honour the &lt;revocation&gt; node inside the XML).
/// </para>
/// <para>
/// Closes ADR-0022 Phase B — removing the EF Core dependency from the
/// Platform.Api host so the only remaining AOT blockers are the SignalR
/// shadow (closed in Phase A) and the AOT publish itself (Phase C).
/// </para>
/// </remarks>
public sealed class DapperXmlRepository : IXmlRepository
{
    private const string SelectAllSql =
        "SELECT id, friendly_name, xml FROM data_protection_keys";

    private const string InsertSql =
        "INSERT INTO data_protection_keys (friendly_name, xml) VALUES (@FriendlyName, @Xml)";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DapperXmlRepository> _logger;

    public DapperXmlRepository(NpgsqlDataSource dataSource, ILogger<DapperXmlRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var conn = _dataSource.OpenConnection();
        var rows = conn.Query<KeyRow>(SelectAllSql).AsList();

        var elements = new List<XElement>(rows.Count);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Xml))
                continue;

            try
            {
                elements.Add(XElement.Parse(row.Xml));
            }
            catch (System.Xml.XmlException ex)
            {
                DapperXmlRepositoryLog.MalformedRow(_logger, row.Id, row.FriendlyName ?? "<null>", ex.Message);
            }
        }

        return elements;
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);

        using var conn = _dataSource.OpenConnection();
        conn.Execute(InsertSql, new
        {
            FriendlyName = friendlyName ?? string.Empty,
            Xml = element.ToString(SaveOptions.DisableFormatting),
        });
    }

    private sealed class KeyRow
    {
        public long Id { get; init; }
        public string? FriendlyName { get; init; }
        public string Xml { get; init; } = string.Empty;
    }
}
