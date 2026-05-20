using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;
using Npgsql;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Identity.DataProtection;

internal static partial class NpgsqlXmlRepositoryLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "[DPK] Skipping malformed row id={Id} friendly={FriendlyName}: {Reason}")]
    public static partial void MalformedRow(ILogger logger, long id, string friendlyName, string reason);
}

/// <summary>
/// AOT-safe raw Npgsql implementation of ASP.NET Core DataProtection's
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
/// <see cref="IXmlRepository"/> is a synchronous interface, so this type uses
/// raw synchronous <see cref="NpgsqlCommand"/> directly (the
/// <c>Verbara.Sdk.Data.Npgsql</c> facade is async-only); the name-based reader
/// getters from that package work on the synchronous reader too.
/// </para>
/// </remarks>
public sealed class NpgsqlXmlRepository : IXmlRepository
{
    private const string SelectAllSql =
        "SELECT id, friendly_name, xml FROM data_protection_keys";

    private const string InsertSql =
        "INSERT INTO data_protection_keys (friendly_name, xml) VALUES (@FriendlyName, @Xml)";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<NpgsqlXmlRepository> _logger;

    public NpgsqlXmlRepository(NpgsqlDataSource dataSource, ILogger<NpgsqlXmlRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var elements = new List<XElement>();

        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(SelectAllSql, conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64("id");
            var friendlyName = reader.GetStringOrNull("friendly_name");
            var xml = reader.GetStringOrNull("xml");

            if (string.IsNullOrWhiteSpace(xml))
                continue;

            try
            {
                elements.Add(XElement.Parse(xml));
            }
            catch (System.Xml.XmlException ex)
            {
                NpgsqlXmlRepositoryLog.MalformedRow(_logger, id, friendlyName ?? "<null>", ex.Message);
            }
        }

        return elements;
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);

        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("FriendlyName", friendlyName ?? string.Empty));
        cmd.Parameters.Add(new NpgsqlParameter("Xml", element.ToString(SaveOptions.DisableFormatting)));
        cmd.ExecuteNonQuery();
    }
}
