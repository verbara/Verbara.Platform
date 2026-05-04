# Contributing to Verbara Platform

Thanks for your interest in contributing to **Verbara Platform** — the .NET 10 Native AOT backend for the Verbara contact-center.

This repository is licensed under the [Apache License 2.0](LICENSE). By submitting a contribution, you agree that your contribution is licensed under the same terms (Apache 2.0 inbound = outbound).

The Pro overlays (`Verbara.Sdk.Pro.*` packages consumed by this project) are commercial and **not** part of this open-source distribution. Contributions to those packages happen in a separate (private) repository.

## Quick start

```bash
# 1. Fork and clone
git clone https://github.com/<your-username>/verbara-platform.git
cd verbara-platform

# 2. Build (.NET 10 SDK required)
dotnet build Asterisk.Platform.slnx

# 3. Run all tests
dotnet test Asterisk.Platform.slnx

# 4. Run the API host locally
cd src/Asterisk.Platform.Api
dotnet run
# → API on http://localhost:5000

# 5. Or run the full stack via docker-compose
cd docker
docker compose -f docker-compose.full.yml up
```

See [`CLAUDE.md`](CLAUDE.md) for the full architecture overview, package layers, and conventions. See [`docs/getting-started.md`](docs/getting-started.md) for a 10-minute walkthrough from clone to running tenant.

## Reporting bugs

Use [GitHub Issues](https://github.com/verbara/verbara-platform/issues) for non-security bugs. Include:

- What you expected to happen.
- What actually happened (stack trace if applicable).
- Steps to reproduce.
- .NET SDK version (`dotnet --info`), OS, Verbara Platform version (in `Directory.Build.props`).
- Relevant log excerpts (please redact PII / tenant data).

For **security vulnerabilities**, do not open a public issue. Email `security@verbara.io` with details. We aim to acknowledge within 72 hours. See [`SECURITY.md`](SECURITY.md) when published, or follow [RFC 9116](https://www.rfc-editor.org/rfc/rfc9116) conventions in the meantime.

## Suggesting features

Open a GitHub Discussion (or Issue with the `enhancement` label) describing:

- The problem you are trying to solve.
- Why this fits Verbara Platform's scope (which package layer it belongs to — Core, Channels, AI/Workflow, Cross-cutting, Storage, or Host).
- Any prior art or links to similar features in other tools.
- Whether it depends on Pro overlays (most enterprise features do; check `Pro.*` package usage).

Larger features (multi-week) require a `docs/specs/<YYYY-MM-DD>-<topic>.md` proposal before code review. The `docs/` tree is the authoritative workstream — open new plans there.

## Pull request process

1. **Branch** off `main`. Use a descriptive branch name: `feat/skill-routing-engine`, `fix/queue-overflow-edge-case`, `docs/contributing-update`.
2. **Commit** using [Conventional Commits](https://www.conventionalcommits.org/):
   - `feat: add skill-based routing engine`
   - `fix: handle queue overflow when capacity is zero`
   - `docs: clarify multi-tenant resolver flow`
   - `refactor:`, `test:`, `chore:`, `perf:` are also accepted.
3. **Write tests** when changing behavior. xUnit + FluentAssertions for unit tests. Integration tests under `tests/Asterisk.Platform.Api.Tests/` use a real PostgreSQL via Testcontainers — **do not mock the database** (we got burned in the past by mock-vs-prod divergence).
4. **Run locally before pushing:**
   ```bash
   dotnet build Asterisk.Platform.slnx -c Release  # TreatWarningsAsErrors=true is enforced
   dotnet test Asterisk.Platform.slnx               # all tests must pass
   ```
5. **Open a PR** against `main` with:
   - A summary of what changed and why.
   - Link to the related issue / discussion / spec.
   - Test plan in the description.
   - Mention any new dependencies in `Directory.Packages.props`.
6. **Sign your commits with DCO** (Developer Certificate of Origin) — append `-s` to `git commit`:
   ```bash
   git commit -s -m "feat: add skill-based routing engine"
   ```
   This adds a `Signed-off-by:` line and certifies you wrote the code or have the right to contribute it. We do not require a CLA at this time; DCO is sufficient.
7. **Wait for review.** A maintainer reviews within a few business days. Address feedback by pushing additional commits to the same branch.

## Coding standards

### Native AOT compatibility (non-negotiable)

This is a **Native AOT** project. `IsAotCompatible=true` and `TreatWarningsAsErrors=true` across all packages.

- **No reflection.** Use source generators for JSON serialization (`[JsonSerializable]` in a `JsonSerializerContext`).
- **No dynamic code generation** (no `Expression.Compile`, no `Activator.CreateInstance` with non-AOT-safe types).
- **All async APIs** return `ValueTask<T>` or `Task<T>` — no `IAsyncEnumerable<T>` without justification.
- **Logging:** use `ILogger<T>` with source-generated logging (`[LoggerMessage]`) for hot paths.

### Test naming

Tests follow the `Method_ShouldExpected_WhenCondition` convention:

```csharp
[Fact]
public void RouteAsync_ShouldFallbackToBusinessHours_WhenAllAgentsOffline() { ... }
```

### Other conventions (see `CLAUDE.md` for the full list)

- **Conventional Commits**, no `Co-Authored-By` lines.
- **TreatWarningsAsErrors must remain ON** across all repos — zero tolerance for warnings.
- **DI extension per package**: each `Verbara.Platform.X` package has a single `AddVerbaraX(this IServiceCollection)` extension method.
- **Multi-tenant safety**: every persistence operation must respect tenant scoping. See [ADR-0002](docs/decisions/0002-tenant-stamping-pipeline-end-to-end.md).
- **Spanish for conversation, English for code/commits/docs.**

## i18n (server-side messages)

Platform serves messages to the Web frontend. User-facing string literals must support i18n via the resource pattern. The Web layer enforces locale parity ([Web ADR-0001](https://github.com/verbara/verbara-web/blob/main/docs/decisions/0001-i18n-parity-ci-gate.md)) — keep server message keys consistent with the Web's `public/locales/*.json` structure.

## License of contributions

By contributing to this repository, you agree that:

- Your contributions are licensed under the [Apache License 2.0](LICENSE) (inbound = outbound).
- You have the right to submit the contribution (the DCO sign-off attests to this).
- You retain copyright on your contribution; you grant Verbara and downstream users the rights described in Apache 2.0.

We do not require a CLA at this time. If we ever need to relicense this codebase, we will ask contributors at that time and respect anyone who declines.

## Pro overlays — out of scope here

The `Verbara.Sdk.Pro.*` packages (multi-tenant, advanced analytics, cluster, licensing, dialer) are commercial closed-source. Platform consumes them via NuGet. Bug reports about Pro behavior are welcome (we'll triage and forward); contributions to Pro source code are handled privately.

For commercial Pro licensing inquiries: `licensing@verbara.io`.

## Questions

Open a [Discussion](https://github.com/verbara/verbara-platform/discussions), or reach `hello@verbara.io`.
