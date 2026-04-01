using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Serialization;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresMessageStore : IMessageStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresMessageStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(Message message, CancellationToken ct)
    {
        var contentJson = JsonSerializer.Serialize(message.Content, ConversationsJsonContext.Default.MessageEnvelope);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO messages (message_id, conversation_id, tenant_id, direction, channel, sender_id, content, " +
            "delivery_status, external_message_id, created_at, delivered_at, read_at, updated_at, created_by, updated_by) " +
            "VALUES (@MessageId, @ConversationId, @TenantId, @Direction, @Channel, @SenderId, @Content::jsonb, " +
            "@DeliveryStatus, @ExternalMessageId, @CreatedAt, @DeliveredAt, @ReadAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, message_id) DO UPDATE SET " +
            "  delivery_status = EXCLUDED.delivery_status, delivered_at = EXCLUDED.delivered_at, " +
            "  read_at = EXCLUDED.read_at, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by",
            new
            {
                MessageId = message.MessageId.Value,
                ConversationId = message.ConversationId.Value,
                TenantId = message.TenantId.Value,
                Direction = (int)message.Direction,
                Channel = (int)message.Channel,
                message.SenderId,
                Content = contentJson,
                DeliveryStatus = (int)message.DeliveryStatus,
                message.ExternalMessageId,
                message.CreatedAt,
                message.DeliveredAt,
                message.ReadAt,
                message.UpdatedAt,
                message.CreatedBy,
                message.UpdatedBy,
            });
    }

    public async Task<Message?> GetByIdAsync(TenantId tenantId, EntityId messageId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<MessageRow>(
            "SELECT message_id, conversation_id, tenant_id, direction, channel, sender_id, content, " +
            "delivery_status, external_message_id, created_at, delivered_at, read_at, updated_at, created_by, updated_by " +
            "FROM messages WHERE tenant_id = @TenantId AND message_id = @MessageId",
            new { TenantId = tenantId.Value, MessageId = messageId.Value });
        return row?.ToMessage();
    }

    public async Task<IReadOnlyList<Message>> GetConversationMessagesAsync(
        TenantId tenantId, EntityId conversationId, int limit, int offset, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MessageRow>(
            "SELECT message_id, conversation_id, tenant_id, direction, channel, sender_id, content, " +
            "delivery_status, external_message_id, created_at, delivered_at, read_at, updated_at, created_by, updated_by " +
            "FROM messages WHERE tenant_id = @TenantId AND conversation_id = @ConversationId " +
            "ORDER BY created_at LIMIT @Limit OFFSET @Offset",
            new { TenantId = tenantId.Value, ConversationId = conversationId.Value, Limit = limit, Offset = offset });
        return rows.Select(r => r.ToMessage()).ToList();
    }

    public async Task UpdateDeliveryStatusAsync(
        TenantId tenantId, EntityId messageId, MessageDeliveryStatus status, DateTimeOffset? timestamp, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE messages SET delivery_status = @Status, " +
            "  delivered_at = CASE WHEN @Status = @DeliveredInt THEN @Timestamp ELSE delivered_at END, " +
            "  read_at = CASE WHEN @Status = @ReadInt THEN @Timestamp ELSE read_at END " +
            "WHERE tenant_id = @TenantId AND message_id = @MessageId",
            new
            {
                TenantId = tenantId.Value,
                MessageId = messageId.Value,
                Status = (int)status,
                DeliveredInt = (int)MessageDeliveryStatus.Delivered,
                ReadInt = (int)MessageDeliveryStatus.Read,
                Timestamp = timestamp,
            });
    }

    public async Task<Message?> FindByExternalIdAsync(TenantId tenantId, string externalMessageId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<MessageRow>(
            "SELECT message_id, conversation_id, tenant_id, direction, channel, sender_id, content, " +
            "delivery_status, external_message_id, created_at, delivered_at, read_at, updated_at, created_by, updated_by " +
            "FROM messages WHERE tenant_id = @TenantId AND external_message_id = @ExternalMessageId",
            new { TenantId = tenantId.Value, ExternalMessageId = externalMessageId });
        return row?.ToMessage();
    }

    public async Task<IReadOnlyList<Message>> GetByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
            return [];

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ids = conversationIds.Select(id => id.Value).ToArray();
        var rows = await conn.QueryAsync<MessageRow>(
            "SELECT message_id, conversation_id, tenant_id, direction, channel, sender_id, content, " +
            "delivery_status, external_message_id, created_at, delivered_at, read_at, updated_at, created_by, updated_by " +
            "FROM messages WHERE tenant_id = @TenantId AND conversation_id = ANY(@ConversationIds) " +
            "ORDER BY created_at",
            new { TenantId = tenantId.Value, ConversationIds = ids });
        return rows.Select(r => r.ToMessage()).ToList();
    }

    public async Task<int> DeleteByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
            return 0;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ids = conversationIds.Select(id => id.Value).ToArray();
        return await conn.ExecuteAsync(
            "DELETE FROM messages WHERE tenant_id = @TenantId AND conversation_id = ANY(@ConversationIds)",
            new { TenantId = tenantId.Value, ConversationIds = ids });
    }

    public async Task<int> DeleteOrphanedAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM messages m WHERE m.tenant_id = @TenantId " +
            "AND NOT EXISTS (SELECT 1 FROM conversations c WHERE c.tenant_id = m.tenant_id AND c.conversation_id = m.conversation_id)",
            new { TenantId = tenantId.Value });
    }

    private sealed record MessageRow(
        string message_id,
        string conversation_id,
        string tenant_id,
        int direction,
        int channel,
        string? sender_id,
        string content,
        int delivery_status,
        string? external_message_id,
        DateTimeOffset created_at,
        DateTimeOffset? delivered_at,
        DateTimeOffset? read_at,
        DateTimeOffset? updated_at,
        string? created_by,
        string? updated_by)
    {
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
