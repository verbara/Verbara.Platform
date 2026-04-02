using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresFlowExecutionStore : IFlowExecutionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresFlowExecutionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(FlowExecution execution, CancellationToken ct)
    {
        var variablesJson = JsonSerializer.Serialize(execution.Variables, PostgresJson.Ctx.DictionaryStringString);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO flow_executions (execution_id, flow_id, flow_version, tenant_id, conversation_id, " +
            "current_node_id, status, variables, started_at, completed_at, step_count) " +
            "VALUES (@ExecutionId, @FlowId, @FlowVersion, @TenantId, @ConversationId, " +
            "@CurrentNodeId, @Status, @Variables::jsonb, @StartedAt, @CompletedAt, @StepCount) " +
            "ON CONFLICT (execution_id) DO UPDATE SET " +
            "  current_node_id = EXCLUDED.current_node_id, status = EXCLUDED.status, " +
            "  variables = EXCLUDED.variables, completed_at = EXCLUDED.completed_at, step_count = EXCLUDED.step_count",
            new
            {
                ExecutionId = execution.ExecutionId.Value,
                FlowId = execution.FlowId.Value,
                execution.FlowVersion,
                TenantId = execution.TenantId.Value,
                ConversationId = execution.ConversationId.Value,
                CurrentNodeId = execution.CurrentNodeId.Value,
                Status = (int)execution.Status,
                Variables = variablesJson,
                execution.StartedAt,
                execution.CompletedAt,
                execution.StepCount,
            });
    }

    public async Task<FlowExecution?> GetByIdAsync(EntityId executionId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ExecutionRow>(
            "SELECT execution_id, flow_id, flow_version, tenant_id, conversation_id, current_node_id, " +
            "status, variables, started_at, completed_at, step_count " +
            "FROM flow_executions WHERE execution_id = @ExecutionId",
            new { ExecutionId = executionId.Value });
        return row?.ToExecution();
    }

    public async Task<FlowExecution?> GetActiveByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ExecutionRow>(
            "SELECT execution_id, flow_id, flow_version, tenant_id, conversation_id, current_node_id, " +
            "status, variables, started_at, completed_at, step_count " +
            "FROM flow_executions " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId " +
            "  AND status IN (@Running, @WaitingForInput) " +
            "LIMIT 1",
            new
            {
                TenantId = tenantId.Value,
                ConversationId = conversationId.Value,
                Running = (int)FlowExecutionStatus.Running,
                WaitingForInput = (int)FlowExecutionStatus.WaitingForInput,
            });
        return row?.ToExecution();
    }

    private sealed class ExecutionRow
    {
        public string execution_id { get; init; } = null!;
        public string flow_id { get; init; } = null!;
        public int flow_version { get; init; }
        public string tenant_id { get; init; } = null!;
        public string conversation_id { get; init; } = null!;
        public string current_node_id { get; init; } = null!;
        public int status { get; init; }
        public string variables { get; init; } = null!;
        public DateTime started_at { get; init; }
        public DateTime? completed_at { get; init; }
        public int step_count { get; init; }

        public FlowExecution ToExecution()
        {
            var vars = JsonSerializer.Deserialize(variables, PostgresJson.Ctx.DictionaryStringString)
                       ?? new Dictionary<string, string>();
            return new FlowExecution
            {
                ExecutionId = EntityId.From(execution_id),
                FlowId = EntityId.From(flow_id),
                FlowVersion = flow_version,
                TenantId = new TenantId(tenant_id),
                ConversationId = EntityId.From(conversation_id),
                CurrentNodeId = EntityId.From(current_node_id),
                Status = (FlowExecutionStatus)status,
                Variables = vars,
                StartedAt = started_at,
                CompletedAt = completed_at,
                StepCount = step_count,
            };
        }
    }
}
