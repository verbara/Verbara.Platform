# v1.5.0 "Production Ready" — Implementation Plan

## Context

v1.4.1 delivered ACD distribution, SSE events, and conversation timeouts. Deep analysis revealed critical gaps blocking Partner sales: broken bot handoff, no WebChat transport, missing supervisor digital monitoring, empty report templates, zero health checks, no migration runner, hardcoded secrets. v1.5.0 fixes all of these across 6 sub-projects.

**Spec:** `docs/superpowers/specs/2026-04-09-v150-production-ready-design.md`

## Approach

Use Subagent-Driven Development with FCM batching. 6 sub-projects across 4 phases:

```
Phase 1: Sub-project A (Critical Fixes)           — foundational, unblocks everything
Phase 2: Sub-project B (WebChat) + C (Agent)       — parallel, demo-critical
Phase 3: Sub-project D (Reports) + E (Hardening)   — parallel
Phase 4: Sub-project F (Twilio SMS + Cases)         — market expansion
```

## Sub-projects

### A: Critical Fixes (~15 tests)
1. Bot handoff execution in WebhookEndpoints (TransferToQueue + EndConversation)
2. Hold/Unhold endpoints + switchboard methods
3. Outbound conversation creation POST /conversations
4. Error handling expansion (PlatformException, ArgumentException, traceId)

**Key files:** WebhookEndpoints.cs, ConversationEndpoints.cs, ConversationSwitchboard.cs, ErrorHandlingMiddleware.cs

### B: WebChat End-to-End (~12 tests)
1. WebSocketWebChatTransport implementing IWebChatTransport
2. WebChat HTTP endpoints (session creation, WebSocket upgrade, REST fallback)
3. Customer WebChat widget (vanilla JS, embeddable)
4. Branding integration (widget consumes existing /branding/{tenantId})

**Key files:** New WebSocketWebChatTransport.cs, WebChatEndpoints.cs, widget.js, Program.cs

### C: Agent Workspace Completion (~15 tests)
1. Canned responses backend (model, ICannedResponseStore, InMemory+Postgres, admin+agent API)
2. Supervisor digital conversation monitoring (5 new endpoints)
3. Frontend supervisor digital tab + canned responses hook

**Key files:** New CannedResponse.cs, ICannedResponseStore.cs, CannedResponseEndpoints.cs, SupervisorEndpoints.cs, migration 014

### D: Report Templates (~13 tests)
1. IReportDataBuilder interface + registry
2. AgentPerformanceReportBuilder
3. QueueAnalyticsReportBuilder
4. ConversationSummaryReportBuilder
5. Report type validation on schedule creation
6. Bot analytics aggregation (IBotAnalyticsStore, persistence service, endpoint)

**Key files:** New IReportDataBuilder.cs, 3 builder files, ReportSchedulerService.cs, BotAnalyticsPersistenceService.cs, migration 014

### E: Production Hardening (~10 tests)
1. Health checks (Postgres, BackgroundService heartbeat, AMI)
2. Database migration runner (auto-apply SQL at startup)
3. Config validation at startup (environment-aware)
4. Secrets audit (move defaults to appsettings.Development.json)

**Key files:** New Health/*.cs, DatabaseMigrationService.cs, Program.cs, appsettings*.json

### F: Twilio SMS + Cases API (~14 tests)
1. TwilioSmsProvider (raw HTTP, AOT-compatible)
2. Twilio webhook inbound parsing + HMAC validation
3. Conditional DI registration
4. Cases API (5 endpoints, PostgresCaseStore)

**Key files:** New TwilioSmsProvider.cs, SmsWebhookHandler.cs, CaseEndpoints.cs, PostgresCaseStore.cs

## Verification

1. `dotnet build Asterisk.Platform.slnx` — 0 warnings
2. `dotnet test Asterisk.Platform.slnx` — all ~1,636 tests pass
3. Bot handoff: verify TransferToQueue routes conversation to queue
4. WebChat: WebSocket connects, messages flow end-to-end
5. Health: `/health/ready` returns Postgres status
6. Migration runner: fresh Postgres gets all 14 migrations applied
7. Config validation: production mode fails without required config
8. Reports: scheduled report generates PDF with real data
9. Twilio: SMS send/receive with mock HTTP
10. Cases: CRUD operations with tenant isolation

## Estimated scope
- ~30 new files, ~15 modified files
- ~79 new tests (1,557 → ~1,636)
- Migration 014 (canned_responses + bot_analytics + cases)
- 20 new API endpoints
