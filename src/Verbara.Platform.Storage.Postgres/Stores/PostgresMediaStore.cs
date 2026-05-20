using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Media;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresMediaStore : IMediaStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresMediaStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<MediaFile?> GetByIdAsync(TenantId tenantId, EntityId fileId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT file_id, tenant_id, file_name, content_type, size_bytes, storage_path, conversation_id, uploaded_at, uploaded_by " +
            "FROM media_files WHERE tenant_id = @TenantId AND file_id = @FileId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("FileId", fileId.Value)); },
            MediaRow.Map, ct);
        return row?.ToMediaFile();
    }

    public async Task SaveAsync(MediaFile file, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO media_files (file_id, tenant_id, file_name, content_type, size_bytes, storage_path, conversation_id, uploaded_at, uploaded_by) " +
            "VALUES (@FileId, @TenantId, @FileName, @ContentType, @SizeBytes, @StoragePath, @ConversationId, @UploadedAt, @UploadedBy) " +
            "ON CONFLICT (tenant_id, file_id) DO UPDATE SET " +
            "  file_name = EXCLUDED.file_name, content_type = EXCLUDED.content_type, " +
            "  size_bytes = EXCLUDED.size_bytes, storage_path = EXCLUDED.storage_path, " +
            "  conversation_id = EXCLUDED.conversation_id, uploaded_by = EXCLUDED.uploaded_by",
            p =>
            {
                p.Add(new NpgsqlParameter("FileId",         file.FileId.Value));
                p.Add(new NpgsqlParameter("TenantId",       file.TenantId.Value));
                p.Add(new NpgsqlParameter("FileName",       file.FileName));
                p.Add(new NpgsqlParameter("ContentType",    file.ContentType));
                p.Add(new NpgsqlParameter("SizeBytes",      file.SizeBytes));
                p.Add(new NpgsqlParameter("StoragePath",    file.StoragePath));
                p.Add(new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = (object?)file.ConversationId?.Value ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UploadedAt",     file.UploadedAt));
                p.Add(new NpgsqlParameter("UploadedBy", NpgsqlDbType.Text) { Value = (object?)file.UploadedBy ?? DBNull.Value });
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId fileId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM media_files WHERE tenant_id = @TenantId AND file_id = @FileId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("FileId", fileId.Value)); },
            ct);
    }

    public async Task<IReadOnlyList<MediaFile>> GetByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT file_id, tenant_id, file_name, content_type, size_bytes, storage_path, conversation_id, uploaded_at, uploaded_by " +
            "FROM media_files WHERE tenant_id = @TenantId AND conversation_id = @ConversationId ORDER BY uploaded_at",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("ConversationId", conversationId.Value)); },
            MediaRow.Map, ct);
        return rows.Select(r => r.ToMediaFile()).ToList();
    }

    private sealed class MediaRow
    {
        public string file_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string file_name { get; init; } = null!;
        public string content_type { get; init; } = null!;
        public long size_bytes { get; init; }
        public string storage_path { get; init; } = null!;
        public string? conversation_id { get; init; }
        public DateTime uploaded_at { get; init; }
        public string? uploaded_by { get; init; }

        public static MediaRow Map(NpgsqlDataReader r) => new()
        {
            file_id         = r.GetString("file_id"),
            tenant_id       = r.GetString("tenant_id"),
            file_name       = r.GetString("file_name"),
            content_type    = r.GetString("content_type"),
            size_bytes      = r.GetInt64("size_bytes"),
            storage_path    = r.GetString("storage_path"),
            conversation_id = r.GetStringOrNull("conversation_id"),
            uploaded_at     = r.GetDateTime("uploaded_at"),
            uploaded_by     = r.GetStringOrNull("uploaded_by"),
        };

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
