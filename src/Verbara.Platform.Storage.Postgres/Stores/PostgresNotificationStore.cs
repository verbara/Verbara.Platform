using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core.Notifications;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresNotificationStore : INotificationStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresNotificationStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "notification_id, tenant_id, user_id, category, severity, type, title, body, " +
        "action_url, is_read, created_at, read_at";

    public async ValueTask<Notification?> GetAsync(string notificationId, CancellationToken ct = default)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM notifications WHERE notification_id = @NotificationId",
            p => p.Add(new NpgsqlParameter("NotificationId", notificationId)),
            NotificationRow.Map, ct);

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
        List<NotificationRow> rows;

        if (unreadOnly == true)
        {
            rows = await _dataSource.QueryListAsync(
                $"SELECT {SelectColumns} FROM notifications " +
                "WHERE tenant_id = @TenantId AND user_id = @UserId AND is_read = false " +
                "ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId));
                    p.Add(new NpgsqlParameter("UserId",   userId));
                    p.Add(new NpgsqlParameter("Limit",    limit));
                    p.Add(new NpgsqlParameter("Offset",   offset));
                },
                NotificationRow.Map, ct);
        }
        else
        {
            rows = await _dataSource.QueryListAsync(
                $"SELECT {SelectColumns} FROM notifications " +
                "WHERE tenant_id = @TenantId AND user_id = @UserId " +
                "ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId));
                    p.Add(new NpgsqlParameter("UserId",   userId));
                    p.Add(new NpgsqlParameter("Limit",    limit));
                    p.Add(new NpgsqlParameter("Offset",   offset));
                },
                NotificationRow.Map, ct);
        }

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async ValueTask<int> CountUnreadAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        return (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM notifications " +
            "WHERE tenant_id = @TenantId AND user_id = @UserId AND is_read = false",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId)); p.Add(new NpgsqlParameter("UserId", userId)); },
            ct) ?? 0L);
    }

    public async ValueTask SaveAsync(Notification notification, CancellationToken ct = default)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO notifications " +
            "(notification_id, tenant_id, user_id, category, severity, type, title, body, " +
            " action_url, is_read, created_at, read_at) " +
            "VALUES (@NotificationId, @TenantId, @UserId, @Category, @Severity, @Type, @Title, @Body, " +
            "        @ActionUrl, @IsRead, @CreatedAt, @ReadAt) " +
            "ON CONFLICT (notification_id) DO UPDATE SET " +
            "  is_read = EXCLUDED.is_read, " +
            "  read_at = EXCLUDED.read_at",
            p =>
            {
                p.Add(new NpgsqlParameter("NotificationId", notification.NotificationId));
                p.Add(new NpgsqlParameter("TenantId",       notification.TenantId));
                p.Add(new NpgsqlParameter("UserId", NpgsqlDbType.Text) { Value = (object?)notification.UserId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Category",       (int)notification.Category));
                p.Add(new NpgsqlParameter("Severity",       (int)notification.Severity));
                p.Add(new NpgsqlParameter("Type",           notification.Type));
                p.Add(new NpgsqlParameter("Title",          notification.Title));
                p.Add(new NpgsqlParameter("Body",           notification.Body));
                p.Add(new NpgsqlParameter("ActionUrl", NpgsqlDbType.Text) { Value = (object?)notification.ActionUrl ?? DBNull.Value });
                p.Add(new NpgsqlParameter("IsRead",         notification.IsRead));
                p.Add(new NpgsqlParameter("CreatedAt",      notification.CreatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("ReadAt", NpgsqlDbType.TimestampTz) { Value = (object?)notification.ReadAt?.UtcDateTime ?? DBNull.Value });
            },
            ct);
    }

    public async ValueTask MarkReadAsync(string notificationId, CancellationToken ct = default)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE notifications SET is_read = true, read_at = @ReadAt " +
            "WHERE notification_id = @NotificationId",
            p => { p.Add(new NpgsqlParameter("NotificationId", notificationId)); p.Add(new NpgsqlParameter("ReadAt", DateTime.UtcNow)); },
            ct);
    }

    public async ValueTask MarkAllReadAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE notifications SET is_read = true, read_at = @ReadAt " +
            "WHERE tenant_id = @TenantId AND user_id = @UserId AND is_read = false",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("UserId",   userId));
                p.Add(new NpgsqlParameter("ReadAt",   DateTime.UtcNow));
            },
            ct);
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

        public static NotificationRow Map(NpgsqlDataReader r) => new()
        {
            notification_id = r.GetString("notification_id"),
            tenant_id       = r.GetString("tenant_id"),
            user_id         = r.GetStringOrNull("user_id"),
            category        = r.GetInt32("category"),
            severity        = r.GetInt32("severity"),
            type            = r.GetString("type"),
            title           = r.GetString("title"),
            body            = r.GetString("body"),
            action_url      = r.GetStringOrNull("action_url"),
            is_read         = r.GetBoolean("is_read"),
            created_at      = r.GetDateTime("created_at"),
            read_at         = r.GetDateTimeOrNull("read_at"),
        };

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
