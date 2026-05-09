# gitleaks audit — 2026-05-08

**Tool:** `gitleaks detect` (apt-installed, Debian package)
**Scope:** full git history, 761 commits scanned
**Result:** 6 findings, **all benign** (test fixtures + self-signed demo cert)
**Verdict:** ✅ **clean** — safe for going public per ADR-0018 trigger 1

## Findings (all reviewed; none are real secrets)

| # | Rule | File | Line | In HEAD? | Verdict |
|---|---|---|---|---|---|
| 1 | `generic-api-key` | `tests/Verbara.Platform.Api.Tests/AgentAssistFeatureEndpointsTests.cs` | 76 | ✓ | False positive — `"dg_live_1234567890"` (sequential digits, obvious test placeholder) |
| 2 | `generic-api-key` | `tests/Verbara.Platform.Storage.InMemory.Tests/InMemoryWebhookSubscriptionStoreTests.cs` | 17 | ✓ | False positive — `"test-secret-1234567890123456789..."` (literal `test-secret-` prefix) |
| 3 | `generic-api-key` | `docs/superpowers/plans/2026-04-01-plan30d-outbound-webhooks.md` | 1900 | ✗ | False positive — same fake value as #2, copied into a doc; doc was deleted during Option K migration (`c397e4da`) |
| 4 | `private-key` | `docker/demo/certs/asterisk.key` | 1 | ✓ | False positive — self-signed demo cert for local Docker test environment, never used in production |
| 5 | `generic-api-key` | `tests/Asterisk.Platform.Api.Tests/CampaignApiFactory.cs` | 26 | ✗ | False positive — `"campaign-test-key-99999"` (literal `test-key-99999`); file was renamed during Verbara rebrand and the literal was carried forward into the new `Verbara.Platform.Api.Tests/` namespace |
| 6 | `generic-api-key` | `tests/Asterisk.Platform.Api.Tests/AuthenticatedPlatformApiFactory.cs` | 17 | ✗ | False positive — `"test-api-key-12345"` (literal `test-api-key-12345`); same renaming story as #5 |

## Detail

### In-HEAD findings (3)

**#1 — `AgentAssistFeatureEndpointsTests.cs:76`** — Test invokes the AgentAssist feature toggle endpoint with a fake provider credential payload. The value `"dg_live_1234567890"` mimics the format of a Deepgram production API key (`dg_live_*` prefix) but the contents are sequential digits — clearly a placeholder. Used only in unit tests; never sent to a real Deepgram endpoint.

**#2 — `InMemoryWebhookSubscriptionStoreTests.cs:17`** — Test fixture for the in-memory webhook store. The HMAC `Secret` field is set to `"test-secret-1234567890123456789012345678901234567890"` — literal `test-secret-` prefix + sequential digits. Test-only.

**#4 — `docker/demo/certs/asterisk.key`** — Self-signed RSA private key for the demo Docker stack's TLS endpoint. Lives next to the public cert and is part of the demo's "clone, run, see it work" experience. Never used to encrypt real customer data.

### History-only findings (3)

**#3 — `docs/superpowers/plans/2026-04-01-plan30d-outbound-webhooks.md:1900`** — A planning doc that quoted the same fake test secret as #2 inline as an example. The entire `docs/superpowers/` tree was deleted during the Option K layout migration (commit `c397e4da`); local-only docs are now gitignored per CLAUDE.md.

**#5 / #6 — `tests/Asterisk.Platform.Api.Tests/*Factory.cs`** — Test factory base classes with `public const string TestApiKey = "campaign-test-key-99999"` (and similar). Files were moved/renamed during the Verbara rebrand (`Asterisk.Platform.Api.Tests/` → `Verbara.Platform.Api.Tests/`). The fake values are carried into the new namespace path — no real key was ever stored.

## Action plan

- **No history rewrite needed** — every finding is a demonstrable test fixture or demo cert.
- **No rotation needed** — no real secret was ever exposed.
- **For going public (per ADR-0018 trigger 1):** accepted. The current findings would still appear post-flip but pose zero risk.

### Optional hygiene (not required for the trigger)

1. **Demo cert (#4):** consider regenerating at first-run via the Docker compose entrypoint instead of committing. Better practice for OSS demo repos. Tracked separately if pursued.
2. **`.gitleaks.toml` allowlist** to suppress these specific test fixtures from future scans, reducing noise. Optional — current 6-leak baseline is acceptable as long as the audit doc explains each.

## Re-scan command

```sh
gitleaks detect --source . --no-banner
```

Expected baseline: 6 findings, all matching the table above. Run on every release; investigate any new findings beyond this baseline.

## Cross-references

- Audit context: SDK auto-memory `project_2026_05_08_licensing_audit.md`
- Trigger source: this repo's ADR-0018 trigger 1
- Active plan: `docs/plans/active/2026-05-08-visibility-decision-and-alignment.md`
