using Npgsql;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres <see cref="IContactAuditWriter"/> (contacts-writes-follow-their-permissions, design D17): the contact
/// statement of <see cref="PostgresContactStore"/> and the insert of <see cref="PostgresAuditStore"/>, the same SQL and
/// parameters, on one connection inside one transaction of the data source both stores share. Nothing commits until
/// both statements have run; a failure or a cancellation before the commit rolls both back when the transaction is
/// disposed. The precedent is <see cref="PostgresTypificationCorrectionAuditWriter"/>.
/// </summary>
internal sealed class PostgresContactAuditWriter : IContactAuditWriter
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly Action? _afterContactStatement;

    public PostgresContactAuditWriter(NpgsqlDataSource dataSource)
        : this(dataSource, afterContactStatement: null)
    {
    }

    /// <summary>
    /// For tests: <paramref name="afterContactStatement"/> runs after the contact statement and before the audit insert,
    /// so a test can cancel the call between the two writes.
    /// </summary>
    internal PostgresContactAuditWriter(NpgsqlDataSource dataSource, Action? afterContactStatement)
    {
        _dataSource = dataSource;
        _afterContactStatement = afterContactStatement;
    }

    public async Task SaveAsync(Contact contact, AuditEntry auditEntry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(auditEntry);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await conn.ExecuteAsync(
            PostgresContactStore.SaveSql, p => PostgresContactStore.AddSaveParameters(p, contact), tx, ct).ConfigureAwait(false);
        await InsertAuditEntryAsync(conn, tx, auditEntry, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(TenantId tenantId, EntityId contactId, AuditEntry auditEntry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auditEntry);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var deleted = await conn.ExecuteAsync(
            PostgresContactStore.DeleteSql, p => PostgresContactStore.AddDeleteParameters(p, tenantId, contactId), tx, ct)
            .ConfigureAwait(false);
        if (deleted == 0)
            return false; // nothing deleted, so nothing to record; disposing the transaction rolls it back

        await InsertAuditEntryAsync(conn, tx, auditEntry, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task InsertAuditEntryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, AuditEntry auditEntry, CancellationToken ct)
    {
        _afterContactStatement?.Invoke();
        ct.ThrowIfCancellationRequested();
        await conn.ExecuteAsync(
            PostgresAuditStore.InsertSql, p => PostgresAuditStore.AddInsertParameters(p, auditEntry), tx, ct).ConfigureAwait(false);
    }
}
