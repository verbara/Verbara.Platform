# Harness scenario — exactly-once — ❌ FAIL

- **Topology:** talos
- **Started:** 2026-05-24T18:03:31.0011678+00:00
- **Completed:** 2026-05-24T18:03:38.6119570+00:00
- **Duration:** 7.61 s
- **Pods observed:** 4
- **SignalR clients:** 5
- **Events emitted:** 10
- **Expected receives per client:** 10

## Receives per client (each must equal expected)

| Client | Received |
|---:|---:|
| 0 | 0 |
| 1 | 0 |
| 2 | 0 |
| 3 | 0 |
| 4 | 0 |

## Per-pod outcome rollup (audit endpoint)

| Pod | Forwarded | SkippedNotLeader | Audit URL |
|---|---:|---:|---|
| platform-realtime-d89df6ccb-76mx4 | 0 | 0 | http://localhost:15031 |
| platform-realtime-d89df6ccb-bmkkg | 0 | 0 | http://localhost:15032 |
| platform-realtime-d89df6ccb-t95x2 | 0 | 0 | http://localhost:15033 |
| platform-realtime-d89df6ccb-xnf74 | 0 | 0 | http://localhost:15034 |

- **Total Forwarded:** 0
- **Total SkippedNotLeader:** 0
- **Leader pod(s):** (none)

## Failures

- ❌ Total Forwarded mismatch: expected 10, got 0. Per-pod: platform-realtime-d89df6ccb-76mx4=0, platform-realtime-d89df6ccb-bmkkg=0, platform-realtime-d89df6ccb-t95x2=0, platform-realtime-d89df6ccb-xnf74=0.
- ❌ Total SkippedNotLeader mismatch: expected 30, got 0. Per-pod: platform-realtime-d89df6ccb-76mx4=0, platform-realtime-d89df6ccb-bmkkg=0, platform-realtime-d89df6ccb-t95x2=0, platform-realtime-d89df6ccb-xnf74=0.
- ❌ Expected exactly 1 leader pod (multi-leader = broken lock semantics). Got 0: [].
- ❌ Client #0 received 0 OnAgentStateChanged message(s), expected 10. Indicates lost message.
- ❌ Client #1 received 0 OnAgentStateChanged message(s), expected 10. Indicates lost message.
- ❌ Client #2 received 0 OnAgentStateChanged message(s), expected 10. Indicates lost message.
- ❌ Client #3 received 0 OnAgentStateChanged message(s), expected 10. Indicates lost message.
- ❌ Client #4 received 0 OnAgentStateChanged message(s), expected 10. Indicates lost message.

