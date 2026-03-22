# CLAUDE.md

## Project Overview

Asterisk.Platform is an omnichannel contact center framework. .NET 10 Native AOT.

**24 packages, 781 tests, 0 warnings, AOT-compatible:**

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
| Platform.Channels.Video | Video connector, DI | 26 |
| Platform.Channels.Twitter | Twitter connector, DI | 27 |
| Platform.Channels.Rcs | RCS connector, DI | 33 |
| Platform.Routing.Inbound | Inbound routing pipeline — channel mapping, last-agent, priority, overflow, business hours, DI | 32 |
| Platform.Switchboard | Conversation ownership lifecycle — assign, offer, accept, reject, transfer, DI | 38 |
| Platform.KnowledgeBase | Knowledge search abstraction, DI | 19 |
| Platform.Flows | DAG workflow engine — 11 node types, persistent execution, template rendering, LLM abstraction, DI | 52 |
| Platform.Bot | Virtual agent orchestration — IVirtualAgent, BotOrchestrator, flow-driven turn management, analytics, DI | 30 |
| Platform.Automation | Automation rules — event triggers, condition evaluator, action executor, DI | 45 |
| Platform.Surveys | Post-conversation surveys — survey store, response collection, DI | 30 |
| Platform.Storage.InMemory | In-memory implementations of all 20 stores — dev/test, DI | 27 |
| Platform.Api | HTTP host — webhook endpoints, agent desktop, admin CRUD, SSE, API-key auth | 48 |

## Build & Test

```sh
# Build entire solution
dotnet build Asterisk.Platform.slnx

# Run all tests
dotnet test Asterisk.Platform.slnx

# Quiet output
dotnet test Asterisk.Platform.slnx -v q
```

## Running the Platform

Platform.Api is the composition root and executable host. It wires all packages together via DI and exposes the HTTP surface.

```sh
cd src/Asterisk.Platform.Api
dotnet run
```

## API Endpoints (34 total)

| Group | Method | Path | Description |
|-------|--------|------|-------------|
| Webhooks | POST | `/api/webhooks/{tenantId}/{channel}` | Inbound channel webhook |
| Webhooks | GET | `/api/webhooks/{tenantId}/whatsapp` | WhatsApp hub verification |
| Webhooks | GET | `/api/webhooks/{tenantId}/messenger` | Messenger hub verification |
| Webhooks | GET | `/api/webhooks/{tenantId}/instagram` | Instagram hub verification |
| Conversations | GET | `/api/conversations` | List conversations |
| Conversations | GET | `/api/conversations/{id}` | Get conversation |
| Conversations | GET | `/api/conversations/{id}/messages` | Get messages |
| Conversations | POST | `/api/conversations/{id}/messages` | Send message |
| Conversations | POST | `/api/conversations/{id}/accept` | Accept conversation |
| Conversations | POST | `/api/conversations/{id}/reject` | Reject conversation |
| Conversations | POST | `/api/conversations/{id}/transfer` | Transfer conversation |
| Conversations | POST | `/api/conversations/{id}/close` | Close conversation |
| Agent Desktop | GET | `/api/agents/me` | Get current agent profile |
| Agent Desktop | PUT | `/api/agents/me/state` | Update agent presence state |
| Admin | GET/POST | `/api/admin/users` | List / create users |
| Admin | GET/PUT/DELETE | `/api/admin/users/{id}` | Get / update / delete user |
| Admin | GET/POST | `/api/admin/queues` | List / create queues |
| Admin | GET/PUT/DELETE | `/api/admin/queues/{id}` | Get / update / delete queue |
| Admin | GET/POST | `/api/admin/agents` | List / create agents |
| Admin | GET/PUT | `/api/admin/agents/{id}` | Get / update agent |
| Admin | GET/POST | `/api/admin/teams` | List / create teams |
| Admin | GET/PUT/DELETE | `/api/admin/teams/{id}` | Get / update / delete team |
| SSE | GET | `/api/events/stream` | Server-Sent Events stream (real-time push) |

Auth: API-key header (`X-Api-Key`) via `ApiKeyAuthenticationHandler`. All non-webhook endpoints require authorization.

## Architecture

### Platform.Api — Composition Root

Platform.Api (`Program.cs`) is the executable host. It registers all packages via DI and maps all HTTP endpoints. Storage.InMemory provides drop-in in-memory implementations of every store for development and testing.

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
services.AddPlatformAutomation();
services.AddPlatformSurveys();
services.AddPlatformStorageInMemory(); // dev/test: all 20 stores in-memory
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
