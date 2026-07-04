# Tasks — credit-grant-lazy-mint-rollover

## 1. Grounding

- [ ] 1.1 Confirm the exact balance-read call-sites on the enforcement/readout paths and the
      grant-existence lookup cost (index usage)

## 2. Implementation

- [ ] 2.1 Inline lazy mint on grant-miss (reuse `PostGrantAsync`; no write on steady-state reads)
- [ ] 2.2 InMemory store mirror

## 3. Verification

- [ ] 3.1 Deterministic rollover test (FakeTimeProvider across the month boundary)
- [ ] 3.2 Concurrent first-read test (exactly one grant + single projection credit)
- [ ] 3.3 Live-DB Postgres ON CONFLICT coverage; `dotnet test` + CI green, zero warnings
