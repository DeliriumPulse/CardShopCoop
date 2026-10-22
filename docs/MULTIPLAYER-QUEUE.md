# Multiplayer improvement queue

Requested on 2026-10-15. Checked items have implementation and automated validation;
they are not a claim of completed native multiplayer testing. Baseline: custom build
1.10.0.1, wire 110. Work from the top unless a new regression blocks play. Audit
existing recovery and retry code before adding another mechanism.

Implemented in 1.10.0.1: current-stock shelf merging and targeted shelf outcomes;
targeted box repair/retry; item reply validation; expired-result replay protection;
stable counter/table controls; per-peer Steam reliable queues and limits; queued
wallet reservations; safer failed-command handling; and connection progress reporting.
See [the reliability audit](reliability-2026-10-15.md) for evidence and remaining limits.

## 1. Guest item reliability — highest priority

- [ ] Build a repeatable host/guest test for taking packs from shelves, returning
  them to held boxes, dropping and retrieving boxes, placing them on racks, opening
  packs, and using spray cans. Include rapid actions, delayed replies and rejoining.
- [ ] Audit every pending pickup/placement path for a final accepted, rejected or
  recovered outcome. Find paths that leave hands or objects locked after the
  transfer is no longer pending.
- [x] Check the existing retry and duplicate-request handling together. A repeated
  request must not move another item, and a late reply must not affect the next
  action or session.

Acceptance: stock totals agree across both PCs; items are neither lost nor
duplicated; guests can continue interacting after a recoverable delay. A genuine
failure explains what happened and offers a usable recovery action.

## 2. Repair shared state with less disruption

- [x] Replace list-position addresses for counter settings and play-table controls
  and props with stable shared identities.
- [ ] Extend the address audit to the remaining register/trade and furniture flows.
- [ ] Investigate small, revision-based comparisons of inventory, held objects,
  registers and furniture to detect disagreement without repeatedly sending the
  whole shop.
- [x] Repair only affected shelf/box objects where safe. Reconcile pending transfers before
  applying a refresh; do not discard their obligations or replay completed actions.

Acceptance: a controlled disagreement converges without a full rejoin, duplicate
purchases, lost stock, or taking an actively held object from another player.

## 3. Stronger joining and recovery from connection interruptions

- [ ] Extend startup tests to cancellation during downloads, scene loading and the
  first shared-object snapshot, including repeated Stop/Host/Join cycles.
- [ ] Investigate bounded automatic reconnect for short interruptions. Revalidate
  compatibility and the host's current world, invalidate old session callbacks and
  settle outstanding item/money actions before resuming play.
- [x] Review reconnect feasibility. Current transaction messages do not all have
  durable operation IDs/outcomes or a persisted guest-hand checkpoint. Automatic
  replay/resume is deferred until these can prevent duplicate charges or lost stock.
- [ ] Keep a clear Cancel/Leave option and an explicit failure when recovery cannot
  establish safe state. Reconnecting must not silently replace a solo save.

Acceptance: old replies cannot modify a replacement session; interrupted joins
release their locks; any supported reconnect restores one consistent shop.
Seamless reconnect remains a feasibility item until demonstrated safely.

## 4. Shared checkout and money consistency

- [ ] Audit checkout, change removal, purchases, bills, hiring and bonuses under
  delayed and repeated requests, including two players spending close together.
- [ ] Verify transaction identity and duplicate protection through error recovery,
  customer reuse and disconnect cleanup. Do not blindly repeat an action whose
  payment outcome is uncertain.
- [x] Keep accepted native money events reserved across frames and release them at
  the actual wallet callback. Prevent automatic dispatch replay of unprotected
  money/inventory commands after a partial failure.

Acceptance: a completed transaction charges or pays once; both PCs agree on change
and balances; a failed operation cannot leave the register unusable.

## 5. Keep sessions responsive as the shop grows

- [ ] Measure outgoing queue age, bytes per update, snapshot duration and main-thread
  work in small and busy shops before changing update rates.
- [x] Implement bounded per-peer Steam traffic budgets so a slow guest or large snapshot
  does not hold up other players' interactions. Preserve ordering between related
  actions and state updates.
- [ ] Check animation, sound and environment updates during recovery. Reconstructing
  state must not replay old purchase sounds or leave spray effects running.

Acceptance: measured improvements under controlled load, bounded queues and no
new inventory, ordering or presentation regressions.

## 6. Explain connection health in the existing report

- [x] Extend the connection report with bounded summaries of queue age, join phase
  stalls, recovery attempts and detected state disagreements where not already
  covered. Prefer useful host-side evidence over requiring extra guest reports.
- [x] Add a simple connection-health indicator based on measured progress,
  rather than claiming that receiving movement packets proves all syncing works.

Acceptance: a report helps distinguish a disconnected peer, a stalled transfer and
an object-state problem. Keep existing privacy protections and avoid packet/save
contents, passwords and unrestricted logging.

## Rules for every change

- Preserve equal co-op access. Do not add host approval prompts or manual permission
  gates for normal guest actions. Any automatic ownership needed to prevent
  conflicting actions must be short-lived, recoverable and explained clearly.
- Make small changes with regression coverage, then verify both roles. Simulated
  delay, lost application replies, repeated requests and session changes belong in
  the tests; native Steam and Unity behavior still require two-PC play.
- Recheck conservation of items and money, cleanup on leaving, and the existing
  [two-PC acceptance tests](../README.md#two-pc-acceptance-test-still-required).
- Follow AGENTS.md versioning rules for changes to message fields, meaning or
  routing. Both players must receive the same build for a multiplayer test.
- Mark work complete only after recording what was tested and any remaining live
  verification. Do not describe a proposed improvement as an implemented fix.
