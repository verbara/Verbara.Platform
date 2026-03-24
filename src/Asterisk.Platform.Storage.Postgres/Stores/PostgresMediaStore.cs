using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Media;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresMediaStore : IMediaStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresMediaStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<MediaFile?> GetByIdAsync(TenantId tenantId, EntityId fileId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<MediaRow>(
            "SELECT file_id, tenant_id, file_name, content_type, size_bytes, storage_path, conversation_id, uploaded_at, uploaded_by " +
            "FROM media_files WHERE tenant_id = @TenantId AND file_id = @FileId",
            new { TenantId = tenantId.Value, FileId = fileId.Value });
        return row?.ToMediaFile();
    }

    public async Task SaveAsync(MediaFile file, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO media_files (file_id, tenant_id, file_name, content_type, size_bytes, storage_path, conversation_id, uploaded_at, uploaded_by) " +
            "VALUES (@FileId, @TenantId, @FileName, @ContentType, @SizeBytes, @StoragePath, @ConversationId, @UploadedAt, @UploadedBy) " +
            "ON CONFLICT (tenant_id, file_id) DO UPDATE SET " +
            "  file_name = EXCLUDED.file_name, content_type = EXCLUDED.content_type, " +
            "  size_bytes = EXCLUDED.size_bytes, storage_path = EXCLUDED.storage_path, " +
            "  conversation_id = EXCLUDED.conversation_id, uploaded_by = EXCLUDED.uploaded_by",
            new
            {
                FileId = file.FileId.Value,
                TenantId = file.TenantId.Value,
                file.FileName,
                file.ContentType,
                file.SizeBytes,
                file.StoragePath,
                ConversationId = file.ConversationId?.Value,
                file.UploadedAt,
                file.UploadedBy,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId fileId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM media_files WHERE tenant_id = @TenantId AND file_id = @FileId",
            new { TenantId = tenantId.Value, FileId = fileId.Value });
    }

    public async Task<IReadOnlyList<MediaFile>> GetByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MediaRow>(
            "SELECT file_id, tenant_id, file_name, content_type, size_bytes, storage_path, conversation_id, uploaded_at, uploaded_by " +
            "FROM media_files WHERE tenant_id = @TenantId AND conversation_id = @ConversationId ORDER BY uploaded_at",
            new { TenantId = tenantId.Value, ConversationId = conversationId.Value });
        return rows.Select(r => r.ToMediaFile()).ToList();
    }

    private sealed record MediaRow(
        string file_id,
        string tenant_id,
        string file_name,
        string content_type,
        long size_bytes,
        string storage_path,
        string? conversation_id,
        DateTimeOffset uploaded_at,
        string? uploaded_by)
    {
        public MediaFile ToMediaFile() => new()
        {
            FileId = EntityId.From(file_id),
            TenantId = new TenantId(tenant_id),
            FileName = file_name,
            ContentType = content_type,
            SizeBytes = size_bytes,
            StoragePath = storage_path,
            ConversationId = conversation_id != null ? EntityId.From(conversation_id) : null,
            UploadedAt = uploaded_at,
            UploadedBy = uploaded_by,
        };
    }
}
