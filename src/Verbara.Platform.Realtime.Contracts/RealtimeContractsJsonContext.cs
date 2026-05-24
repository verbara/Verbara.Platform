using System.Text.Json.Serialization;
using Verbara.Platform.Realtime.Contracts.Dtos;

namespace Verbara.Platform.Realtime.Contracts;

/// <summary>
/// System.Text.Json source-generation context for the cross-process DTOs
/// shared between Verbara.Platform.Api (server) and Verbara.Platform.Realtime
/// (client). Required for the AOT-clean serialization path on Platform.Api
/// (Native AOT post-Phase C) — reflection-based JsonSerializer methods would
/// trip the trim analyzer.
/// </summary>
[JsonSerializable(typeof(AgentTenantResponse))]
[JsonSerializable(typeof(HubAuditEntry))]
[JsonSerializable(typeof(RelayOutcomeEntry))]
[JsonSerializable(typeof(RelayOutcomePage))]
public partial class RealtimeContractsJsonContext : JsonSerializerContext;
