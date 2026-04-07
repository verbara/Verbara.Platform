using Dapper;
using Npgsql;
using Asterisk.Platform.Core.Notifications;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresNotificationStore : INotificationStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresNotificationStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "notification_id, tenant_id, user_id, category, severity, type, title, body, " +
        "action_url, is_read, created_at, read_at";

    public async ValueTask<Notification?> GetAsync(string notificationId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<NotificationRow>(
            $"SELECT {SelectColumns} FROM notifications WHERE notification_id = @NotificationId",
            new { NotificationId = notificationId });

        return row?.ToModel();
    }

    public async ValueTask<IReadOnlyList<Notification>> ListAsync(
        string tenantId,
        string userId,
        bool? unreadOnly,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM notifications " +
                  "WHERE tenant_id = @TenantId AND user_id = @UserId";

        if (unreadOnly == true)
            sql += " AND is_read = false";

        sql += " ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<NotificationRow>(sql, new
        {
            TenantId = tenantId,
            UserId   = userId,
            Limit    = limit,
            Offset   = offset,
        });

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async ValueTask<int> CountUnreadAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM notifications " +
            "WHERE tenant_id = @TenantId AND user_id = @UserId AND is_read = false",
            new { TenantId = tenantId, UserId = userId });
    }

    public async ValueTask SaveAsync(Notification notification, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO notifications " +
            "(notification_id, tenant_id, user_id, category, severity, type, title, body, " +
            " action_url, is_read, created_at, read_at) " +
            "VALUES (@NotificationId, @TenantId, @UserId, @Category, @Severity, @Type, @Title, @Body, " +
            "        @ActionUrl, @IsRead, @CreatedAt, @ReadAt) " +
            "ON CONFLICT (notification_id) DO UPDATE SET " +
            "  is_read = EXCLUDED.is_read, " +
            "  read_at = EXCLUDED.read_at",
            new
            {
                NotificationId = notification.NotificationId,
                TenantId       = notification.TenantId,
                UserId         = notification.UserId,
                Category       = (int)notification.Category,
                Severity       = (int)notification.Severity,
                Type           = notification.Type,
                Title          = notification.Title,
                Body           = notification.Body,
                ActionUrl      = notification.ActionUrl,
                IsRead         = notification.IsRead,
                CreatedAt      = notification.CreatedAt.UtcDateTime,
                ReadAt         = notification.ReadAt?.UtcDateTime,
            });
    }

    public async ValueTask MarkReadAsync(string notificationId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE notifications SET is_read = true, read_at = @ReadAt " +
            "WHERE notification_id = @NotificationId",
            new { NotificationId = notificationId, ReadAt = DateTime.UtcNow });
    }

    public async ValueTask MarkAllReadAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE notifications SET is_read = true, read_at = @ReadAt " +
            "WHERE tenant_id = @TenantId AND user_id = @UserId AND is_read = false",
            new { TenantId = tenantId, UserId = userId, ReadAt = DateTime.UtcNow });
    }

    private sealed class NotificationRow
    {
        public string notification_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string? user_id { get; init; }
        public int category { get; init; }
        public int severity { get; init; }
        public string type { get; init; } = null!;
        public string title { get; init; } = null!;
        public string body { get; init; } = null!;
        public string? action_url { get; init; }
        public bool is_read { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? read_at { get; init; }

        public Notification ToModel() => new()
        {
            NotificationId = notification_id,
            TenantId       = tenant_id,
            UserId         = user_id,
            Category       = (NotificationCategory)category,
            Severity       = (NotificationSeverity)severity,
            Type           = type,
            Title          = title,
            Body           = body,
            ActionUrl      = action_url,
            IsRead         = is_read,
            CreatedAt      = new DateTimeOffset(created_at, TimeSpan.Zero),
            ReadAt         = read_at.HasValue ? new DateTimeOffset(read_at.Value, TimeSpan.Zero) : null,
        };
    }
}
