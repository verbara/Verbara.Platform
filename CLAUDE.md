# CLAUDE.md

## Project Overview

Asterisk.Platform is an omnichannel contact center framework. .NET 10 Native AOT.

**20 packages, 616 tests, 0 warnings, AOT-compatible:**

| Package | Purpose | Tests |
|---------|---------|-------|
| Platform.Core | Abstractions, value objects, base interfaces, DI | 24 |
| Platform.Identity | Users, RBAC, API keys, service accounts, DI | 10 |
| Platform.Conversations | Conversation lifecycle (14 states), Contact CRM-lite, Cases, Tags, DI | 73 |
| Platform.Queues | Queue config, SLA, Agent with per-channel capacity, Teams, DI | 44 |
| Platform.Channels.Core | Channel registry, inbound pipeline, delivery status, tenant config, DI | 38 |
| Platform.Channels.WhatsApp | Meta Business API connector, HMAC webhook, 24h session window, DI | 29 |
| Platform.Channels.Sms | Provider-agnostic SMS connector, segment calculator, DI | 28 |
| Platform.Channels.WebChat | WebChat connector, session manager, transport abstraction, DI | 25 |
| Platform.Channels.Messenger | Messenger connector, DI | 22 |
| Platform.Channels.Instagram | Instagram connector, DI | 16 |
| Platform.Channels.Telegram | Telegram connector, DI | 24 |
| Platform.Channels.Email | Email connector, DI | 41 |
| Platform.Channels.Video | Video connector, DI | — |
| Platform.Channels.Twitter | Twitter connector, DI | 27 |
| Platform.Channels.Rcs | RCS connector, DI | 33 |
| Platform.Routing.Inbound | Inbound routing pipeline — channel mapping, last-agent, priority, overflow, business hours, DI | 32 |
| Platform.Switchboard | Conversation ownership lifecycle — assign, offer, accept, reject, transfer, DI | 23 |
| Platform.KnowledgeBase | Knowledge search abstraction, DI | — |
| Platform.Flows | DAG workflow engine — 11 node types, persistent execution, template rendering, LLM abstraction, DI | 52 |
| Platform.Bot | Virtual agent orchestration — IVirtualAgent, BotOrchestrator, flow-driven turn management, analytics, DI | 30 |

## Build & Test

```sh
# Build entire solution
dotnet build Asterisk.Platform.slnx

# Run all tests
dotnet test Asterisk.Platform.slnx

# Quiet output
dotnet test Asterisk.Platform.slnx -v q
```

## Architecture

### Inbound Message Flow

```
Webhook (WhatsApp/SMS/WebChat)
  → IWebhookHandler.HandleAsync
      → WebhookResult: NewMessage | StatusUpdate | Ignored
  → NewMessage path:
      → IInboundMessagePipeline.ProcessAsync
          → DeduplicateStep (IMessageStore.FindByExternalIdAsync)
          → ContactResolutionStep (IContactIdentityResolver.ResolveAsync)
          → ConversationResolutionStep (IConversationStore + IConversationLifecycleService)
          → MessagePersistenceStep (IMessageStore.SaveAsync)
          → PipelineResult (ConversationId, ContactId, MessageId, IsNewConversation)
      → IInboundRouter.RouteAsync (optional)
          → ChannelQueueMappingMiddleware
          → LastAgentMiddleware
          → PriorityEscalationMiddleware
          → OverflowMiddleware
          → BusinessHoursMiddleware
          → RouteResult (QueueId, AgentId, Priority)
      → IConversationSwitchboard.AssignToQueueAsync / OfferToAgentAsync
  → StatusUpdate path:
      → DeliveryStatusHandler.HandleAsync
          → IMessageStore.FindByExternalIdAsync
          → IMessageStore.UpdateDeliveryStatusAsync
```

### DI Composition Example

```csharp
services.AddPlatformCore();
services.AddPlatformConversations();
services.AddPlatformQueues();
services.AddPlatformChannels();
services.AddWhatsApp(o => { o.PhoneNumberId = "..."; o.AccessToken = "..."; });
services.AddSms(o => { o.DefaultFromNumber = "+15550000000"; });
services.AddWebChat();
services.AddInboundRouting();
services.AddSwitchboard();
services.AddPlatformFlows();   // + register IFlowStore, IFlowExecutionStore, ILlmProvider
services.AddPlatformBot();     // + register IBotConfigStore
```

## Code Conventions

- No `Co-Authored-By` in commits
- AOT: No reflection. `[JsonSerializable]`, `[LoggerMessage]`, static dispatch.
- Async-first with CancellationToken
- Private fields: `_camelCase`
- File-scoped namespaces
- Test naming: `Method_ShouldExpected_WhenCondition`
- Test stack: xunit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0
- TreatWarningsAsErrors ON, WarningLevel 9999
- Central package management in Directory.Packages.props
