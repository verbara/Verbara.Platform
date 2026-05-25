# B-LK.1 sweep summary — Platform v2.5.1 on Talos lab

Parsed from `tests/Verbara.Platform.LoadTests/load-test-reports-archive/<scenario>-<var>-<value>-60s/nbomber_report_*.md`.

## jwt

| Step | OK | Fail | p50 (ms) | p95 (ms) | p99 (ms) | RPS | Status codes |
|---|---|---|---|---|---|---|---|
| 10 | 600 | 0 | 95.87 | 150.27 | 246.14 | 10 | OK=600  |
| 50 | 662 | 110 | 5910.53 | 24150.02 | 26984.45 | 11 | OK=662 InternalServerError=3 ServiceUnavailable=107  |
| 100 | 2683 | 2426 | 25608.19 | 35618.82 | 38502.4 | 44.7 | OK=2683 ServiceUnavailable=2426  |

## queues

| Step | OK | Fail | p50 (ms) | p95 (ms) | p99 (ms) | RPS | Status codes |
|---|---|---|---|---|---|---|---|
| 10 | 600 | 0 | 5.5 | 10.13 | 12.39 | 10 | OK=600  |
| 50 | 2995 | 5 | 4.42 | 7.38 | 10.62 | 49.9 | OK=2995 ServiceUnavailable=5  |
| 100 | 5985 | 15 | 4.22 | 7.65 | 10.65 | 99.8 | OK=5985 ServiceUnavailable=15  |
| 250 | 12685 | 2315 | 4.6 | 7.94 | 14.46 | 211.4 | OK=12685 ServiceUnavailable=1584 Unauthorized=731  |
| 500 | 30000 | 0 | 5.67 | 49.92 | 1600.51 | 500 | OK=30000  |

## livequeue

| Step | OK | Fail | p50 (ms) | p95 (ms) | p99 (ms) | RPS | Status codes |
|---|---|---|---|---|---|---|---|
| 50 | 0 | 3000 | 0 | 0 | 0 | 0 | NotFound=3000  |
| 100 | 0 | 5000 | 0 | 0 | 0 | 0 | NotFound=5000  |
| 250 | 0 | 5000 | 0 | 0 | 0 | 0 | NotFound=5000  |
| 500 | 0 | 5000 | 0 | 0 | 0 | 0 | NotFound=5000  |
| 1000 | 0 | 7447 | 0 | 0 | 0 | 0 | NotFound=7447  |

## agentassist

| Step | OK | Fail | p50 (ms) | p95 (ms) | p99 (ms) | RPS | Status codes |
|---|---|---|---|---|---|---|---|
| 10 | 600 | 0 | 4.74 | 7.08 | 10.3 | 10 | OK=600  |
| 50 | 2993 | 7 | 4.17 | 6.41 | 9.4 | 49.9 | OK=2993 ServiceUnavailable=7  |
| 100 | 6000 | 0 | 4.1 | 8.12 | 11.17 | 100 | OK=6000  |
| 250 | 12819 | 1586 | 4.8 | 8.64 | 13.14 | 213.6 | OK=12819 ServiceUnavailable=1586  |
| 500 | 30000 | 0 | 6.11 | 17.89 | 35.71 | 500 | OK=30000  |

## presence

| Step | OK | Fail | p50 (ms) | p95 (ms) | p99 (ms) | RPS | Status codes |
|---|---|---|---|---|---|---|---|
| 100 | 41906 | 0 | 152.96 | 213.63 | 228.61 | 698.4 | OK=41906  |
| 250 | 5901 | 0 | 394.75 | 509.44 | 596.48 | 98.4 | OK=5901  |
| 500 | 41546 | 0 | 698.88 | 1396.74 | 1511.42 | 692.4 | OK=41546  |
| 1000 | 29421 | 0 | 296.7 | 2496.51 | 11911.17 | 490.4 | OK=29421  |
| 1500 | 34755 | 1000 | 1602.56 | 6475.78 | 12419.07 | 579.2 | OK=34755 Unauthorized=1000  |

