# Harness scenario — exactly-once — ✅ PASS

- **Topology:** talos
- **Started:** 2026-05-24T19:48:21.8643751+00:00
- **Completed:** 2026-05-24T19:48:29.4318163+00:00
- **Duration:** 7.57 s
- **Pods observed:** 4
- **SignalR clients:** 5
- **Events emitted:** 10
- **Expected receives per client:** 10

## Receives per client (each must equal expected)

| Client | Received |
|---:|---:|
| 0 | 10 |
| 1 | 10 |
| 2 | 10 |
| 3 | 10 |
| 4 | 10 |

## Per-pod outcome rollup (audit endpoint)

| Pod | Forwarded | SkippedNotLeader | Audit URL |
|---|---:|---:|---|
| platform-realtime-5f457cc9db-8658q | 0 | 10 | http://localhost:15031 |
| platform-realtime-5f457cc9db-jkxv8 | 0 | 10 | http://localhost:15032 |
| platform-realtime-5f457cc9db-kfnt6 | 10 | 0 | http://localhost:15033 |
| platform-realtime-5f457cc9db-rlrhv | 0 | 10 | http://localhost:15034 |

- **Total Forwarded:** 10
- **Total SkippedNotLeader:** 30
- **Leader pod(s):** platform-realtime-5f457cc9db-kfnt6

