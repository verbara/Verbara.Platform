using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Serialization;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresMessageStore : IMessageStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresMessageStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(Message message, CancellationToken ct)
    {
        var contentJson = JsonSerializer.Serialize(message.Content, ConversationsJsonContext.Default.MessageEnvelope);

        await _dataSource.ExecuteAsync(
            "INSERT INTO messages (message_id, conversation_id, tenant_id, direction, channel, sender_id, content, " +
            "delivery_status, external_message_id, created_at, delivered_at, read_at, updated_at, created_by, updated_by) " +
            "VALUES (@MessageId, @ConversationId, @TenantId, @Direction, @Channel, @SenderId, @Content::jsonb, " +
            "@DeliveryStatus, @ExternalMessageId, @CreatedAt, @DeliveredAt, @ReadAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, message_id) DO UPDATE SET " +
            "  delivery_status = EXCLUDED.delivery_status, delivered_at = EXCLUDED.delivered_at, " +
            "  read_at = EXCLUDED.read_at, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by",
            p =>
            {
                p.Add(new NpgsqlParameter("MessageId",         message.MessageId.Value));
                p.Add(new NpgsqlParameter("ConversationId",    message.ConversationId.Value));
                p.Add(new NpgsqlParameter("TenantId",          message.TenantId.Value));
                p.Add(new NpgsqlParameter("Direction",         (int)message.Direction));
                p.Add(new NpgsqlParameter("Channel",           (int)message.Channel));
                p.Add(new NpgsqlParameter("SenderId", NpgsqlDbType.Text) { Value = (object?)message.SenderId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Content",           contentJson));
                p.Add(new NpgsqlParameter("DeliveryStatus",    (int)message.DeliveryStatus));
                p.Add(new NpgsqlParameter("ExternalMessageId", NpgsqlDbType.Text) { Value = (object?)message.ExternalMessageId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedAt",         message.CreatedAt));
                p.Add(new NpgsqlParameter("DeliveredAt", NpgsqlDbType.TimestampTz) { Value = (object?)message.DeliveredAt ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ReadAt", NpgsqlDbType.TimestampTz) { Value = (object?)message.ReadAt ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = (object?)message.UpdatedAt ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedBy", NpgsqlDbType.Text) { Value = (object?)message.CreatedBy ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UpdatedBy", NpgsqlDbType.Text) { Value = (object?)message.UpdatedBy ?? DBNull.Value });
            },
            ct);
    }

    public async Task<Message?> GetByIdAsync(TenantId tenantId, EntityId messageId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT message_id, conversation_id, tenant_id, direction, channel, sender_id, content, " +
            "delivery_status, external_message_id, created_at, delivered_at, read_at, updated_at, created_by, updated_by " +
            "FROM messages WHERE tenant_id = @TenantId AND message_id = @MessageId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("MessageId", messageId.Value)); },
            MessageRow.Map, ct);
        return row?.ToMessage();
    }

    public async Task<IReadOnlyList<Message>> GetConversationMessagesAsync(
        TenantId tenantId, EntityId conversationId, int limit, int offset, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT message_id, conversation_id, tenant_id, direction, channel, sender_id, content, " +
            "delivery_status, external_message_id, created_at, delivered_at, read_at, updated_at, created_by, updated_by " +
            "FROM messages WHERE tenant_id = @TenantId AND conversation_id = @ConversationId " +
            "ORDER BY created_at LIMIT @Limit OFFSET @Offset",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId",       tenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", conversationId.Value));
                p.Add(new NpgsqlParameter("Limit",          limit));
                p.Add(new NpgsqlParameter("Offset",         offset));
            },
            MessageRow.Map, ct);
        return rows.Select(r => r.ToMessage()).ToList();
    }

    public async Task UpdateDeliveryStatusAsync(
        TenantId tenantId, EntityId messageId, MessageDeliveryStatus status, DateTimeOffset? timestamp, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE messages SET delivery_status = @Status, " +
            "  delivered_at = CASE WHEN @Status = @DeliveredInt THEN @Timestamp ELSE delivered_at END, " +
            "  read_at = CASE WHEN @Status = @ReadInt THEN @Timestamp ELSE read_at END " +
            "WHERE tenant_id = @TenantId AND message_id = @MessageId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId",     tenantId.Value));
                p.Add(new NpgsqlParameter("MessageId",    messageId.Value));
                p.Add(new NpgsqlParameter("Status",       (int)status));
                p.Add(new NpgsqlParameter("DeliveredInt", (int)MessageDeliveryStatus.Delivered));
                p.Add(new NpgsqlParameter("ReadInt",      (int)MessageDeliveryStatus.Read));
                p.Add(new NpgsqlParameter("Timestamp", NpgsqlDbType.TimestampTz) { Value = (object?)timestamp ?? DBNull.Value });
            },
            ct);
    }

    public async Task<Message?> FindByExternalIdAsync(TenantId tenantId, string externalMessageId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT message_id, conversation_id, tenant_id, direction, channel, sender_id, content, " +
            "delivery_status, external_message_id, created_at, delivered_at, read_at, updated_at, created_by, updated_by " +
            "FROM messages WHERE tenant_id = @TenantId AND external_message_id = @ExternalMessageId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("ExternalMessageId", externalMessageId)); },
            MessageRow.Map, ct);
        return row?.ToMessage();
    }

    public async Task<IReadOnlyList<Message>> GetByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
            return [];

        var ids = conversationIds.Select(id => id.Value).ToArray();
        var rows = await _dataSource.QueryListAsync(
            "SELECT message_id, conversation_id, tenant_id, direction, channel, sender_id, content, " +
            "delivery_status, external_message_id, created_at, delivered_at, read_at, updated_at, created_by, updated_by " +
            "FROM messages WHERE tenant_id = @TenantId AND conversation_id = ANY(@ConversationIds) " +
            "ORDER BY created_at",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("ConversationIds", ids)); },
            MessageRow.Map, ct);
        return rows.Select(r => r.ToMessage()).ToList();
    }

    public async Task<int> DeleteByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
            return 0;

        var ids = conversationIds.Select(id => id.Value).ToArray();
        return await _dataSource.ExecuteAsync(
            "DELETE FROM messages WHERE tenant_id = @TenantId AND conversation_id = ANY(@ConversationIds)",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("ConversationIds", ids)); },
            ct);
    }

    public async Task<int> DeleteOrphanedAsync(TenantId tenantId, CancellationToken ct)
    {
        return await _dataSource.ExecuteAsync(
            "DELETE FROM messages m WHERE m.tenant_id = @TenantId " +
            "AND NOT EXISTS (SELECT 1 FROM conversations c WHERE c.tenant_id = m.tenant_id AND c.conversation_id = m.conversation_id)",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            ct);
    }

    private sealed class MessageRow
    {
        public string message_id { get; init; } = null!;
        public string conversation_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public int direction { get; init; }
        public int channel { get; init; }
        public string? sender_id { get; init; }
        public string content { get; init; } = null!;
        public int delivery_status { get; init; }
        public string? external_message_id { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? delivered_at { get; init; }
        public DateTime? read_at { get; init; }
        public DateTime? updated_at { get; init; }
        public string? created_by { get; init; }
        public string? updated_by { get; init; }

        public static MessageRow Map(NpgsqlDataReader r) => new()
        {
            message_id          = r.GetString("message_id"),
            conversation_id     = r.GetString("conversation_id"),
            tenant_id           = r.GetString("tenant_id"),
            direction           = r.GetInt32("direction"),
            channel             = r.GetInt32("channel"),
            sender_id           = r.GetStringOrNull("sender_id"),
            content             = r.GetString("content"),
            delivery_status     = r.GetInt32("delivery_status"),
            external_message_id = r.GetStringOrNull("external_message_id"),
            created_at          = r.GetDateTime("created_at"),
            delivered_at        = r.GetDateTimeOrNull("delivered_at"),
            read_at             = r.GetDateTimeOrNull("read_at"),
            updated_at          = r.GetDateTimeOrNull("updated_at"),
            created_by          = r.GetStringOrNull("created_by"),
            updated_by          = r.GetStringOrNull("updated_by"),
        };

        public Message ToMessage()
        {
            var envelope = JsonSerializer.Deserialize(content, ConversationsJsonContext.Default.MessageEnvelope)
                           ?? new MessageEnvelope([]);
            return new Message
            {
                MessageId = EntityId.From(message_id),
                ConversationId = EntityId.From(conversation_id),
                TenantId = new TenantId(tenant_id),
                Direction = (MessageDirection)direction,
                Channel = (ChannelType)channel,
                SenderId = sender_id,
                Content = envelope,
                DeliveryStatus = (MessageDeliveryStatus)delivery_status,
                ExternalMessageId = external_message_id,
                CreatedAt = created_at,
                DeliveredAt = delivered_at,
                ReadAt = read_at,
                UpdatedAt = updated_at,
                CreatedBy = created_by,
                UpdatedBy = updated_by,
            };
        }
    }
}
