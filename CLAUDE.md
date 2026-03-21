# CLAUDE.md

## Project Overview

Asterisk.Platform is an omnichannel contact center framework. .NET 10 Native AOT.

**4 packages, 113 tests, 0 warnings, AOT-compatible:**

| Package | Purpose | Tests |
|---------|---------|-------|
| Platform.Core | Abstractions, value objects, base interfaces, DI | 24 |
| Platform.Identity | Users, RBAC, API keys, service accounts, DI | 10 |
| Platform.Conversations | Conversation lifecycle (14 states), Contact CRM-lite, Cases, Tags, DI | 50 |
| Platform.Queues | Queue config, SLA, Agent with per-channel capacity, Teams, DI | 29 |

## Build & Test

```sh
dotnet build Asterisk.Platform.slnx
dotnet test Asterisk.Platform.slnx
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
