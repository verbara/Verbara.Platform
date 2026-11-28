using Npgsql;
using NpgsqlTypes;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Mail.Services;

/// <summary>
/// Where the poller stopped reading one mailbox: the UID of the last message it handled, valid only while the
/// mailbox keeps the same <see cref="UidValidity"/> (RFC 3501 §2.3.1.1: a new UIDVALIDITY renumbers every message).
/// </summary>
public sealed record ImapPollPosition(uint UidValidity, uint LastUid);

/// <summary>
/// Keeps the <see cref="ImapPollPosition"/> of each mailbox across restarts of the mail service, so a reply already
/// forwarded to the Api is not forwarded again when the process starts with nothing in memory (#696).
/// </summary>
public interface IImapPollPositionStore
{
    /// <summary>
    /// The stored position of <paramref name="mailbox"/>, or null when none was ever stored. Throws
    /// <see cref="ImapPollPositionTableMissingException"/> while the database is not upgraded yet.
    /// </summary>
    Task<ImapPollPosition?> GetAsync(ImapMailboxOptions mailbox, CancellationToken ct);

    /// <summary>Stores <paramref name="position"/> as the position of <paramref name="mailbox"/>.</summary>
    Task SaveAsync(ImapMailboxOptions mailbox, ImapPollPosition position, CancellationToken ct);
}

/// <summary>
/// Thrown by <see cref="IImapPollPositionStore"/> while the table that keeps the positions does not exist yet: the
/// mail service started before the Api applied migration 034, as it does on an upgrade in the production compose
/// (the Api waits for the mail service to be healthy). The poller waits for it instead of reporting a failure (#702).
/// </summary>
public sealed class ImapPollPositionTableMissingException : Exception
{
    public ImapPollPositionTableMissingException()
        : base("The table mail.imap_poll_positions does not exist yet: the database is not upgraded to migration 034.")
    {
    }

    public ImapPollPositionTableMissingException(string message)
        : base(message)
    {
    }

    public ImapPollPositionTableMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ImapPollPositionTableMissingException(Exception innerException)
        : base("The table mail.imap_poll_positions does not exist yet: the database is not upgraded to migration 034.", innerException)
    {
    }
}

/// <summary>
/// <see cref="IImapPollPositionStore"/> over <c>mail.imap_poll_positions</c> (migration 034), one row per configured
/// mailbox keyed by tenant, host, port, login and folder. Npgsql facade only (no Dapper, ADR-0022).
/// </summary>
internal sealed class PostgresImapPollPositionStore : IImapPollPositionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresImapPollPositionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<ImapPollPosition?> GetAsync(ImapMailboxOptions mailbox, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        try
        {
            return await _dataSource.QueryFirstOrDefaultAsync(
                "SELECT uid_validity, last_uid FROM mail.imap_poll_positions " +
                "WHERE tenant_id = @TenantId AND host = @Host AND port = @Port AND username = @Username AND folder = @Folder",
                p => AddKey(p, mailbox),
                r => new ImapPollPosition(
                    checked((uint)r.GetInt64("uid_validity")),
                    checked((uint)r.GetInt64("last_uid"))),
                ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsTableMissing(ex))
        {
            throw new ImapPollPositionTableMissingException(ex);
        }
    }

    public async Task SaveAsync(ImapMailboxOptions mailbox, ImapPollPosition position, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(position);
        try
        {
            await _dataSource.ExecuteAsync(
                "INSERT INTO mail.imap_poll_positions (tenant_id, host, port, username, folder, uid_validity, last_uid, updated_at) " +
                "VALUES (@TenantId, @Host, @Port, @Username, @Folder, @UidValidity, @LastUid, now()) " +
                "ON CONFLICT (tenant_id, host, port, username, folder) " +
                "DO UPDATE SET uid_validity = EXCLUDED.uid_validity, last_uid = EXCLUDED.last_uid, updated_at = now()",
                p =>
                {
                    AddKey(p, mailbox);
                    p.Add(new NpgsqlParameter("UidValidity", NpgsqlDbType.Bigint) { Value = (long)position.UidValidity });
                    p.Add(new NpgsqlParameter("LastUid", NpgsqlDbType.Bigint) { Value = (long)position.LastUid });
                },
                ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsTableMissing(ex))
        {
            throw new ImapPollPositionTableMissingException(ex);
        }
    }

    // 42P01 undefined_table, also what Postgres answers when the mail schema itself is not there yet (measured).
    private static bool IsTableMissing(PostgresException ex) => ex.SqlState == PostgresErrorCodes.UndefinedTable;

    private static void AddKey(NpgsqlParameterCollection p, ImapMailboxOptions mailbox)
    {
        p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = mailbox.TenantId });
        p.Add(new NpgsqlParameter("Host", NpgsqlDbType.Text) { Value = mailbox.Host });
        p.Add(new NpgsqlParameter("Port", NpgsqlDbType.Integer) { Value = mailbox.Port });
        p.Add(new NpgsqlParameter("Username", NpgsqlDbType.Text) { Value = mailbox.Username });
        p.Add(new NpgsqlParameter("Folder", NpgsqlDbType.Text) { Value = mailbox.Folder });
    }
}
