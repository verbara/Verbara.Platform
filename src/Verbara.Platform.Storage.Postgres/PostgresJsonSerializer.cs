using System.Text.Json;
using System.Text.Json.Serialization;
using Verbara.Platform.Automation;
using Verbara.Platform.Billing;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Serialization;
using Verbara.Platform.Core;
using Verbara.Platform.Flows;
using Verbara.Platform.Queues;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Storage.Postgres;

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<ChannelAddress>))]
[JsonSerializable(typeof(IReadOnlyList<ChannelAddress>))]
[JsonSerializable(typeof(Dictionary<int, bool>))]
[JsonSerializable(typeof(List<FlowNode>))]
[JsonSerializable(typeof(IReadOnlyList<FlowNode>))]
[JsonSerializable(typeof(FlowEdge))]
[JsonSerializable(typeof(List<FlowEdge>))]
[JsonSerializable(typeof(IReadOnlyList<FlowEdge>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<AutomationCondition>))]
[JsonSerializable(typeof(IReadOnlyList<AutomationCondition>))]
[JsonSerializable(typeof(List<AutomationAction>))]
[JsonSerializable(typeof(IReadOnlyList<AutomationAction>))]
[JsonSerializable(typeof(MessageEnvelope))]
[JsonSerializable(typeof(SlaPolicyTarget))]
[JsonSerializable(typeof(QueueOverflowRule))]
[JsonSerializable(typeof(HoursOfOperation))]
[JsonSerializable(typeof(WrapUpConfig))]
[JsonSerializable(typeof(ChannelCapacity))]
[JsonSerializable(typeof(ChannelCapacityOverride))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(IReadOnlyList<int>))]
[JsonSerializable(typeof(List<SurveyQuestion>))]
[JsonSerializable(typeof(IReadOnlyList<SurveyQuestion>))]
[JsonSerializable(typeof(List<SurveyAnswer>))]
[JsonSerializable(typeof(IReadOnlyList<SurveyAnswer>))]
[JsonSerializable(typeof(List<RateEntry>))]
[JsonSerializable(typeof(IReadOnlyList<RateEntry>))]
[JsonSerializable(typeof(List<RateTier>))]
[JsonSerializable(typeof(IReadOnlyList<RateTier>))]
[JsonSerializable(typeof(List<InvoiceLineItem>))]
[JsonSerializable(typeof(IReadOnlyList<InvoiceLineItem>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(TenantOptions))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class PostgresJsonContext : JsonSerializerContext;

internal static class PostgresJson
{
    internal static readonly PostgresJsonContext Ctx = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });

    internal static string Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);

    internal static T? Deserialize<T>(string? json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (string.IsNullOrEmpty(json))
            return default;
        return JsonSerializer.Deserialize(json, typeInfo);
    }
}
