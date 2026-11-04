# Measurement plan: contact-history-opens-by-assignment (frozen before any run)

Cells T-1 to T-4 of OpenSpec change `contact-history-opens-by-assignment`, task 0.3 (verbara-meta/ADR-0030 phase 0).
This file is committed before any benchmark or Postgres measurement of the change runs, and it is not edited after.
Bytes decide and time informs. Every run goes under `verbara-meta/scripts/xr-heavy-lock.sh`.

The text between the two markers is a verbatim copy of the task 0.3 block. Its sha256 is
`a761a6da2b06279c0a2ad27047791664b01477a33ba0f60f9291e793c5fdeded`, and every cell's `run.json` records it.
Re-check it before a run (task 3.6):

    awk '/^<!-- frozen-plan:begin -->$/{f=1;next} /^<!-- frozen-plan:end -->$/{f=0} f' \
      tests/Verbara.Platform.Benchmarks/Predictions/contact-history-opens-by-assignment.md | sha256sum

<!-- frozen-plan:begin -->
  - **T-1, M-cost ABAB.** `ConversationClaimCostBench` (`Inbound`, `Reply`), interleaved A/B/A/B. A is the merge
    base with `main`, B is the candidate, on the same .NET SDK and ISA.
    - *Prediction:* per operation, B allocates between 0 and 1 KiB more than A on each benchmark (one
      `ConversationAudience` per publish, the D7 CASE text being a constant). B's median time is within 0.2 ms of
      A's.
    - *Flip rule:* if, on either benchmark, both B runs allocate more than A's larger run by over 4 KiB or 15 %,
      stop. Record it and return D7 and D17 to review before 3.1. A time delta over 1 ms on loopback is recorded
      and raised to the operator, and does not stop the change on its own.
    - *Control:* with `AUDIENCE_COST_CONTROL=true` (B only), `ConversationOffer.AudienceOf` also allocates a 2 KiB
      buffer per call, and the run must show the added bytes.
    - *Scope, folded in before any run (spec audit round 2, F4):* `Inbound` calls only
      `InboundMessagePipeline.ProcessAsync`, which publishes nothing (the inbound `conversation.message` is published
      later, by `WebhookInboundProcessor.DispatchAsync` or `WebChatInboundRouter`). Neither `Inbound` nor `Reply`
      runs the whole-row upsert (`SaveAsync`) that carries the D7 CASE: the Active path commits through the claim
      statements, which do not touch `queue_id`. So T-1 measures four operations, each built only on APIs that exist
      at A and at B:
      - `Inbound` (as today): evidence for the pipeline only. Prediction: B − A between 0 and 1 KiB per op. Under the
        control it must show **no** added bytes, because it never reaches `AudienceOf`; added bytes there mean the
        pipeline publishes, and that is recorded and raised.
      - `Reply` (as today): the send path, which crosses the publish at `DefaultConversationService`. Prediction and
        control as above (the 2 KiB `AudienceOf` buffer per publish must show).
      - `InboundPublish` (new): one webhook message for the seeded Active conversation through
        `WebhookInboundProcessor.ProcessAsync`, the real inbound publishing path, with an in-process event bus. The
        benchmark project references `Verbara.Platform.Api` for it. Prediction and control as `Reply`.
      - `Save` (new): `IConversationStore.SaveAsync` of the seeded conversation, the whole-row Postgres upsert that
        carries the D7 CASE. Prediction: B − A between 0 and 1 KiB per op, median within 0.2 ms. Control: with
        `AUDIENCE_COST_CONTROL=true` (B only) the store is wrapped so `SaveAsync` runs its upsert twice, and the run
        must show the added bytes (about one more `Save` of A).
      - The flip rule above applies to each of the four operations. An operation whose control does not show its
        predicted bytes is void and concludes nothing.
      - Conclusions are limited to the operations measured: P1's audience half from `Reply` and `InboundPublish`,
        its D7 CASE half from `Save`. If `InboundPublish` cannot be built unchanged at both A and B, the card records
        the inbound-publication half of P1 as UNKNOWN, and it is never concluded from `Inbound`.
  - **T-2, M-cost ABAB.** A new `ConversationContentDeliveryBench`: the delivery `.Where` of `StreamEvents` over
    the fixture's recipient classes × the three content event types plus one non-content event. A has the content
    predicate off (today's delivery), B has it on.
    - *Prediction:* 0 B allocated per call in both A and B. B − A is under 20 ns per event and subscriber.
    - *Flip rule:* any allocation per call in both B runs makes the "no allocation at fan-out" premise false. Stop
      and return D17 "Cost" to review. Over 100 ns is recorded and raised.
    - *Control:* a variant whose predicate formats the audience to a string must show bytes per call.
  - **T-3, M-cost.** The typing publish: `GetByIdAsync` on the Live-DB Postgres plus the publish, against the
    publish alone (main). Interleaved A/B/A/B.
    - *Prediction:* B adds one loopback round trip (0.05 to 1 ms) and 1 to 8 KiB per frame.
    - *Flip rule:* a B median over 2 ms or over 16 KiB per frame adds a per-socket leading-edge throttle in this
      change: at most one typing publish per second per socket. Then re-measure. A result inside the prediction
      changes nothing.
    - *Control:* a variant that reads the conversation twice must show about twice the added bytes.
  - **T-4, M on Postgres (task 3.3).** The summary query on a contact with 100 conversations of 1,000 messages
    each, and a page of 100.
    - *Prediction:* `EXPLAIN (ANALYZE)` uses `idx_conversations_contact` and `idx_messages_conversation`, with no
      sequential scan on `messages`. Warm execution takes at most 50 ms.
    - *Flip rule:* a sequential scan on `messages`, or over 200 ms, stops the change and returns D6 to review.
      Between 50 and 200 ms is recorded and raised to the operator.
    - *Control:* the same query with `enable_indexscan = off` must show the sequential scan.
  - **P5 stays UNKNOWN** (per-stream classification): no cell. It becomes one only on an observed stream-open
    complaint.
  - **The note reads get no cell, and no "negligible" (spec audit round 2, F5; design P6).** The coaching note and
    the transfer note each add one primary-key read per request. That is a class-L fact (`SupervisorEndpoints.cs:231`,
    `ConversationEndpoints.cs:357`). Neither endpoint is a benchmark-covered hot path, so ADR-0030 owes no M-cost cell.
    Their frequency ("rare next to messages") is an unmeasured assumption. No measurement of this change concludes
    that the added read is negligible: the cards and the ADR record P6 as one read per note request plus that
    unmeasured frequency assumption. It becomes a cell only on an observed note-latency complaint.

<!-- frozen-plan:end -->
