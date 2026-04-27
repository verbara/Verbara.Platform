# SIPp Scenarios — R5.5 Production Validation

Reproducible SIP load scenarios driving baseline + stress + soak runs
against Asterisk in the docker-compose.full.yml staging stack (Phase B-L)
and against Kamailio + Asterisk-on-K8s once Phase 0LK lands (Phase B-LK).

These scenarios were authored as part of R5.5 A.3
(`docs/plans/active/2026-04-27-r5.5-execution-plan.md` line 2870) and
are kept independent of the runtime so they can be executed against
either path without modification.

## Scenarios

| #  | File                    | What it exercises                                              | Why it matters                                                             |
|----|-------------------------|----------------------------------------------------------------|----------------------------------------------------------------------------|
| 01 | `01-basic-call.xml`     | INVITE / 200 / ACK / 5 s RTP / BYE — PCMU                      | Per-call signaling baseline + media path                                   |
| 02 | `02-ivr-navigation.xml` | 3× DTMF via SIP INFO (RFC 6086) `application/dtmf-relay`       | Out-of-band DTMF reception by IVR / Read() dialplan apps                   |
| 03 | `03-queue-join.xml`     | INVITE → Queue() with 30 s wait → BYE                          | `app_queue` hold path, AvgWaitSeconds gauge, queue-depth metering          |
| 04 | `04-conference.xml`     | Single-leg ConfBridge() join, 60 s hold                        | ConfBridge() admission control + RTP mixer cost (multi-instance scales it) |
| 05 | `05-transfer.xml`       | INVITE → 200 → REFER (RFC 3515 blind transfer) → NOTIFY → BYE  | REFER processing path + dialplan transfer destination resolution           |

Each scenario is a SIPp UAC (caller side); Asterisk acts as UAS via the
dialplan extension wired to the `service` argument when the runner is
invoked. Phase B-L is responsible for provisioning the dialplan
(currently `pjsip show endpoints` returns 0 — endpoints land in B-L
prep, not A.3).

## Prerequisites

Install SIPp 3.6+ (Debian / Ubuntu package `sip-tester`):

```bash
sudo apt install -y sip-tester
sipp -v
```

The provisioning step (Asterisk PJSIP endpoint + dialplan exten that
maps `[service]` to Queue() / Read() / ConfBridge() / Bridge for transfer)
lives in Phase B-L. Without it the scenarios will receive 404 / 480
responses.

## Running individual scenarios

```bash
# Basic call against queue-1 of medium-loadtest
sipp -sf tests/sipp-scenarios/01-basic-call.xml \
     -s queue-1 \
     -d 5000 -l 10 -r 1 -m 100 \
     -trace_msg -trace_err \
     127.0.0.1:5060
```

Common SIPp flags:

| Flag | Meaning |
|------|---------|
| `-sf <path>`         | Scenario file. |
| `-s <user>`          | Value for `[service]` placeholder in scenario. |
| `-d <ms>`            | Per-call hold time (overrides `<pause>` durations on some scenarios). |
| `-l <N>`             | Max simultaneous calls. |
| `-r <N>`             | Calls per second. |
| `-m <N>`             | Total calls before stop. |
| `-key <name> <val>`  | Per-scenario named arg substitution. Scenario 05 uses `-key xfer_target sip:queue-2@host` to override the REFER target. |
| `-trace_stat`        | Periodic CSV stats. |
| `-stf <file>`        | Stat file destination. |
| `-screen_file <file>`| Captures the live console log. |

## Running the full suite

```bash
./scripts/sipp-test.sh <ASTERISK_OR_KAMAILIO_IP>
```

`scripts/sipp-test.sh` iterates the 5 scenarios sequentially with
canonical defaults (1 cps, 10 max simultaneous, 100 calls each) and
writes per-scenario CSV stats + screen log into
`sipp-reports/<timestamp>/`. Adjust the inner flags in the script for
heavier runs (e.g. Phase C-L stress sweeps push `-r 50 -l 500 -m 5000`).

## Phase coverage

| Phase | What runs |
|-------|-----------|
| **B-L baseline** | All 5 scenarios @ canonical defaults against docker-compose.full.yml Asterisk. Captures per-scenario p99 signaling RTT + ASR + RTP packet-loss baseline. |
| **B-LK baseline** | Same 5 scenarios against Kamailio (hostNetwork SBC) → Asterisk pods. Captures the SBC overhead delta. |
| **C-L stress** | Sweep r=1..50 cps × l=10..500 to find Asterisk per-instance breaking point. |
| **C-LK stress** | Same sweep against Kamailio dispatcher → 1..N Asterisk pods to validate horizontal scale. |
| **D-L / D-LK soak** | 24 h continuous run of scenario 03 + 01 mix at 10 cps to expose memory leaks / fd leaks / DB connection drift. |

## Authoring conventions

- `[service]` is the only required argument; everything else is a
  named `-key <name>` override (scenario 05's `xfer_target` is the
  current example).
- `<ResponseTimeRepartition>` + `<CallLengthRepartition>` ranges are
  tuned per scenario to expose the latency tail expected for that
  shape — they show up in SIPp's CSV / `-trace_stat` output.
- `optional="true"` is used liberally on provisional responses
  (100 / 180 / 183) so scenarios don't fail when Asterisk skips the
  intermediate phases.
- All scenarios use PCMU / RTP/AVP 0 to match the staging Asterisk
  default codec policy and avoid RTP transcoding cost in the
  baseline numbers.
