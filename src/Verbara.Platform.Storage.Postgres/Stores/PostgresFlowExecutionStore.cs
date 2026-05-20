using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Flows;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresFlowExecutionStore : IFlowExecutionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresFlowExecutionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(FlowExecution execution, CancellationToken ct)
    {
        var variablesJson = JsonSerializer.Serialize(execution.Variables, PostgresJson.Ctx.DictionaryStringString);

        await _dataSource.ExecuteAsync(
            "INSERT INTO flow_executions (execution_id, flow_id, flow_version, tenant_id, conversation_id, " +
            "current_node_id, status, variables, started_at, completed_at, step_count) " +
            "VALUES (@ExecutionId, @FlowId, @FlowVersion, @TenantId, @ConversationId, " +
            "@CurrentNodeId, @Status, @Variables::jsonb, @StartedAt, @CompletedAt, @StepCount) " +
            "ON CONFLICT (execution_id) DO UPDATE SET " +
            "  current_node_id = EXCLUDED.current_node_id, status = EXCLUDED.status, " +
            "  variables = EXCLUDED.variables, completed_at = EXCLUDED.completed_at, step_count = EXCLUDED.step_count",
            p =>
            {
                p.Add(new NpgsqlParameter("ExecutionId",    execution.ExecutionId.Value));
                p.Add(new NpgsqlParameter("FlowId",         execution.FlowId.Value));
                p.Add(new NpgsqlParameter("FlowVersion",    execution.FlowVersion));
                p.Add(new NpgsqlParameter("TenantId",       execution.TenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", execution.ConversationId.Value));
                p.Add(new NpgsqlParameter("CurrentNodeId",  execution.CurrentNodeId.Value));
                p.Add(new NpgsqlParameter("Status",         (int)execution.Status));
                p.Add(new NpgsqlParameter("Variables",      variablesJson));
                p.Add(new NpgsqlParameter("StartedAt",      execution.StartedAt));
                p.Add(new NpgsqlParameter("CompletedAt", NpgsqlDbType.TimestampTz) { Value = (object?)execution.CompletedAt ?? DBNull.Value });
                p.Add(new NpgsqlParameter("StepCount",      execution.StepCount));
            },
            ct);
    }

    public async Task<FlowExecution?> GetByIdAsync(EntityId executionId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT execution_id, flow_id, flow_version, tenant_id, conversation_id, current_node_id, " +
            "status, variables, started_at, completed_at, step_count " +
            "FROM flow_executions WHERE execution_id = @ExecutionId",
            p => p.Add(new NpgsqlParameter("ExecutionId", executionId.Value)),
            ExecutionRow.Map, ct);
        return row?.ToExecution();
    }

    public async Task<FlowExecution?> GetActiveByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            "SELECT execution_id, flow_id, flow_version, tenant_id, conversation_id, current_node_id, " +
            "status, variables, started_at, completed_at, step_count " +
            "FROM flow_executions " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId " +
            "  AND status IN (@Running, @WaitingForInput) " +
            "LIMIT 1",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId",        tenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId",  conversationId.Value));
                p.Add(new NpgsqlParameter("Running",         (object)(int)FlowExecutionStatus.Running));
                p.Add(new NpgsqlParameter("WaitingForInput", (object)(int)FlowExecutionStatus.WaitingForInput));
            },
            ExecutionRow.Map, ct);
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

        public static ExecutionRow Map(NpgsqlDataReader r) => new()
        {
            execution_id    = r.GetString("execution_id"),
            flow_id         = r.GetString("flow_id"),
            flow_version    = r.GetInt32("flow_version"),
            tenant_id       = r.GetString("tenant_id"),
            conversation_id = r.GetString("conversation_id"),
            current_node_id = r.GetString("current_node_id"),
            status          = r.GetInt32("status"),
            variables       = r.GetString("variables"),
            started_at      = r.GetDateTime("started_at"),
            completed_at    = r.GetDateTimeOrNull("completed_at"),
            step_count      = r.GetInt32("step_count"),
        };

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
