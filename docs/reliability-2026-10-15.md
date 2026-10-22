# Reliability implementation audit — 2026-10-15

Build: custom 1.10.0.1, wire 110. This record separates code/test evidence from
native multiplayer results, which are still outstanding.

## Implemented

- `WorldSync` merges explicit shelf deltas into current stock through
  `ItemTransferRules`. Reply state describes the affected compartment; queued and
  unsent local pickups remain protected from snapshot overwrites. A duplicate
  request returns its first outcome plus a fresh read of current shelf stock.
- `WorldSync` and `BoxEngine` reject mismatched or impossible item replies before
  removing the pending request or its retry. Stock changes measured after a native
  fault are retained when consistent with that operation.
- `HostTransferAcks` retains a bounded expired-sequence floor per connection. An
  evicted request cannot execute again as a new move; the peer receives a rejoin
  error instead. Mutations are blocked during the rejected peer's disconnect window.
- `BoxRepairRequest` requests one box. Its retry continues until a box snapshot
  arrives. This preserves other pending transfers and avoids refreshing every mod
  module after an ordinary suppressed box snapshot.
- `ReliableSendQueue` preserves FIFO within each Steam peer, budgets 256 KiB per
  peer within the 1 MiB pump budget, and rotates the first peer served. Refused
  sends do not block other peers. A 15-second refusal, native send exception, or
  per-peer overflow explicitly ends the affected connection. Limits are 8,192
  queued frames and 256 MiB of logical queued bytes per peer.
- Counter settings and play-table state/controls use stable identities. Table
  mirror cleanup targets the original object and preserves a local minigame.
- `QueuedSpendLedger` observes only accepted native debit events and releases each
  reservation after `CPlayerData.CPlayer_OnReduceCoin`. It handles an event object
  queued more than once, and includes queued host purchases in guest affordability.
  The existing per-frame reservation still covers costs not yet handed to the
  native event queue.
- `MessageRetryRules` blocks automatic re-execution of purchase, staff, shop,
  settings, grading, trade and economy-contribution commands after a dispatch fault.
  These lack a retained per-operation outcome and may already have side effects.
  Sequence-protected item requests retain their retry behavior.
- Connection help and reports show pending item age separately from send backlog,
  refused-send duration, targeted repair retries, invalid replies, and queued money.
  Receive recency is not a claim that inventory is synchronized. LAN/TCP queue
  measurements are explicitly unavailable rather than fabricated as empty.

## Evidence

- 245 custom regression checks, including concurrent queue enqueues, per-peer
  ordering/budgets, refusals, native send exceptions, overflow and queued spending.
- 33 production transfer/hand/identity checks with game boundaries simulated:
  customer purchases between view and action, lost and duplicate replies, rapid
  pickups, exact-item protection, rejected-item cleanup, returns while holding a
  box, spray guard release, expired history and reordered/removed furniture.
- 41 presentation/wire/integration checks, including nullable targeted shelf state,
  stable counter/table IDs, targeted box repair, real installed-game money-hook
  signatures and prevention of partial-charge dispatch replay.
- Existing 22 box-label, 12 shelf-to-box executor and 25 Steam lobby callback checks.
- Total: 378 checks. Compile uses the installed game 1.02 assemblies. Native APIs
  were inspected in the local decompilation before changing patches.

The three-peer bulk simulation drains 4,000 healthy-peer 1 KiB updates in eight
budgeted pumps while retaining the blocked peer's 2,000 frames. This is a queue
scheduling result, not a measurement of game frame rates or actual Steam throughput.

These are automated code and simulated boundary checks. They do not demonstrate
native rack placement/physics, actual spraying, real Steam behavior, or frame rates
in a busy shop. Use the README acceptance scenarios with both roles and three
players for the slow-peer isolation check. No save data was used as a test fixture.

## Reconnect feasibility and remaining work

Save loading and worker callbacks already check the session generation. Steam
create/join cancellation has simulated callback coverage from 1.9.2.9; cancellation
during a real world download/load and first object snapshot still needs broader
coverage and a native test.

Automatic reconnect is not enabled. Several shared spending/delivery messages have
no durable operation ID and outcome, and guest-held inventory is not a persisted
transaction checkpoint. Replaying these messages or restoring a hand from a stale
snapshot after an uncertain disconnect could duplicate a charge or lose stock.
Implement a host-held transaction/hand checkpoint and reconciliation before adding
automatic resume; existing accepted operations must not be replayed blindly.

Whole-shop revision/digest comparison, remaining register/trade address migration,
full pickup/use/drop/rack native scenarios, and actual busy-shop measurements remain
in the queue. This release implements targeted repair for known transfer failures;
it does not claim to discover every kind of silent state drift.
