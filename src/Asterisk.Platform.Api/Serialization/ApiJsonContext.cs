using System.Text.Json.Serialization;
using Asterisk.Platform.Api.Endpoints;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Switchboard;

namespace Asterisk.Platform.Api.Serialization;

[JsonSerializable(typeof(PagedResult<User>))]
[JsonSerializable(typeof(PagedResult<Queue>))]
[JsonSerializable(typeof(PagedResult<Agent>))]
[JsonSerializable(typeof(PagedResult<Team>))]
[JsonSerializable(typeof(PagedResult<Conversation>))]
[JsonSerializable(typeof(PagedResult<Contact>))]
[JsonSerializable(typeof(PagedResult<ApiKey>))]
[JsonSerializable(typeof(PagedResult<CampaignSummaryDto>))]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(Queue))]
[JsonSerializable(typeof(Agent))]
[JsonSerializable(typeof(Team))]
[JsonSerializable(typeof(Conversation))]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(Contact))]
[JsonSerializable(typeof(FlowDefinition))]
[JsonSerializable(typeof(ApiKey))]
[JsonSerializable(typeof(OwnershipResult))]
[JsonSerializable(typeof(ConversationAssignedEvent))]
[JsonSerializable(typeof(ConversationMessageEvent))]
[JsonSerializable(typeof(ConversationStateChangedEvent))]
[JsonSerializable(typeof(AgentStateChangedEvent))]
[JsonSerializable(typeof(CampaignStatusChangedEvent))]
[JsonSerializable(typeof(CampaignMetricsUpdatedEvent))]
[JsonSerializable(typeof(CampaignDispositionSubmittedEvent))]
[JsonSerializable(typeof(CampaignSummaryDto))]
[JsonSerializable(typeof(CampaignDetailDto))]
[JsonSerializable(typeof(ScheduleDayDto))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class ApiJsonContext : JsonSerializerContext;
