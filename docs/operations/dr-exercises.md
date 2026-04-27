# DR Exercise Log

This document logs the monthly chaos test exercises required by
`docs/operations/backup-disaster-recovery.md`. Each exercise gets one entry,
appended chronologically.

## Cadence

First Monday of each month, 14:00 UTC, against staging.

## Exercise template

```markdown
## DR Exercise — YYYY-MM-DD

**Scenario:** [disk loss | corruption | network partition | etc.]
**Started:** HH:MM UTC
**Backup source:** [filename + S3 path]
**Recovery completed:** HH:MM UTC
**Total duration:** [X minutes]
**Target:** < 30 min
**Met target:** [yes | no]

### Issues encountered
- [...]

### Improvements identified
- [...]

### Sign-off
[Operator name + date]
```

## Exercise log

(No exercises logged yet. First entry should be the R5.4 ship-time chaos test
on staging — see B.3 verification gate.)
