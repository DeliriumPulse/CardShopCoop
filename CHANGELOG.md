# CardShopCoop — Changelog

True co-op multiplayer for TCG Card Shop Simulator. Both players must run the
**same version** — the join handshake enforces it.

---

## 1.0.20
**Fixed: a guest buying furniture (e.g. the play table) charged them but nothing arrived.**
- When a guest ordered furniture, the host spawned it through the game's placement code, which runs an interactive-move cleanup step meant for when *you* drag-and-drop an object. With no drag in progress that step hit a null reference and aborted the delivery halfway — so the furniture never finished spawning even though the money was already taken. The cleanup is now skipped when there's nothing to clean up, and the delivery completes normally.

Both players must update — the launcher does it automatically.

## 1.0.19
**Fixes fake graded cards, wrong card names, and a stuck-box loop.**
- **Fixed: traded graded cards turning "fake," and cards showing the wrong name/art** (e.g. "Uncommon Golem" on the wrong picture). One root cause: the customer's trade offer was stored by *reference* to a card object the game reuses for the next customer, so a later trade would silently overwrite it — scrambling the grade and the card identity. Offers are now frozen as copies. A safety net also refuses any card with an impossible grade instead of creating a broken one.
- **Fixed: a box that couldn't fit on a storage rack retrying forever** (log spam every 30s, and boxes left frozen/unpickable). After a few tries it now leaves the box loose and grabbable, and retries only if the rack situation changes. This should also clear the "empty boxes can't be picked up" case where a stuck box was left frozen.

Both players must update — the launcher does it automatically.

## 1.0.18
**Busy shops now mirror every customer + trade fixes.**
- Fixed: in a busy shop, the guest only saw ~20 customers no matter how many the host had (trade/sell customers at counters often among the invisible ones). The fast network lane was collapsing the host's multi-packet customer batches down to one packet per tick, so only a fraction of the crowd ever arrived. All customer packets now go through. (Only showed up with large crowds — smaller shops fit in one packet.)
- Fixed: accepting a customer's sell-in at the asking price forwarded $0.00 and got refused as a lowball. The asking price is now pre-filled, so accepting takes the offer as shown.
- Fixed: cards from a trade sometimes not appearing in an already-open binder — the binder now refreshes after a card change arrives. Card changes are also logged for easier bug reports.

## 1.0.17
**Modded item prices now sync — set prices, market prices, costs, and averages.**
- Continuation of the 1.0.15 discovery: the content-mod framework (EnhancedPrefabLoader) also invisibly reroutes the game's *price* storage for modded items, so the mod had been reading and writing a shadow copy the game never uses.
- Fixed: price tags stuck at "–" on the other player's screen for modded products (set a price, partner never saw it) — both directions, tags update live.
- Fixed: modded items showing $0.00 market price, cost, and **average cost** for the joiner.
- Harmless without content mods; vanilla behavior unchanged.

## 1.0.16
**Restocker workers + trade-screen turn-taking.**
- Fixed: restock workers unable to carry boxes inside — the other player's box sync was knocking boxes out of workers' hands, breaking their carry loop. Worker-held boxes are now fully protected and hidden on the other screen while carried, same as player-held.
- Fixed: the same trade customer being served on both screens at once. Now proper turn-taking — whoever opens the trade first gets it; the other player sees a polite message. Crash-safe: a lost connection releases the hold within seconds.

## 1.0.15
**The big modded-products fix — guests can order any modded product the host has.**
- Discovered by decompiling EnhancedPrefabLoader: it doesn't add modded products to the game's catalog list, it invisibly intercepts the game's *reads* of it. The mod had been reading the raw list and was blind to every modded product on the host side.
- Fixed: guest orders of modded products refunding as "isn't in the host's catalog" even when the host had the product on their shelf with the license unlocked.
- Fixed: modded licenses (Pokémon/Hololive packs, etc.) not unlocking for the other player.
- Fixed: the catalog comparison always reporting "identical (135 products)" regardless of actual content.
- Refund messages now explain the real cause instead of misleadingly blaming the host's save/tutorial.

## 1.0.14
**Stability hardening — a whole class of rare, session-breaking bugs.**
- The game engine creates a permanently-broken "fake" manager if one is looked up during a loading screen. An audit found 27 places this could strike; all fixed.
- Prevents (all rare, all previously permanent until restart): a partner's avatar never reappearing after reconnect; trades/tournaments/register silently going dead mid-session; license/settings/report sync quietly stopping; the wall-repaint and expansion healing being disabled.

## 1.0.13
**Price sync self-healing.**
- Fixed: prices set by one player sometimes never appearing for the other (tag stuck at "–"). Price updates were change-gated only, so one lost transmission stranded them. Prices now also re-send every 30 seconds; a missed update fixes itself within half a minute.
- Price applies are now logged on the receiving side for easier bug reports.

## 1.0.12
**Storage sync verified + hardened; long-standing rack corruption fixed.**
- 1.0.11's storage fix had a one-line bug that silently disabled it (a fake-manager landmine); this release fixes it after an adversarial re-review, and boxes stored on racks now truly appear stored for both players.
- Fixed a long-standing bug where the mod corrupted storage racks — rack slot counts were treated as loose shelf items, spawning phantom items and breaking "put box on rack" until restart (also the source of the `WorldSync apply: index out of range` log spam).
- Fixed unclickable "ghost" boxes: a box taken off a rack by the other player could become permanently un-grabbable.
- Box contents now stay correct through store/carry/unstore — no more item loss or duplication at the rack.
- Wrong-order refund messages corrected (no longer told past-tutorial hosts to "play past the tutorial").

## 1.0.11
**Warehouse-rack storage sync.**
- Fixed: boxes stored on storage/warehouse racks were invisible to the other player's rack — they appeared as loose boxes clipping the shelf, fell off, and dragged the properly-stored originals off too ("boxes all over the place"). Storing a box now registers it in the other player's rack exactly as the game does locally, and **Take Box** works for both players.
- Boxes on racks are owned by their slot — no position fighting or phantom drift.

## 1.0.10
**Day-rollover lighting freeze + missing-wall repaint.**
- Fixed: daylight sky at night for guests. A host day rollover was mis-read as 13 hours of "drift" and collided with the day-change reset, freezing the sky in daylight. Rollovers are now handled only by the day mirror; a frozen sky self-repairs at the next in-game morning.
- Fixed: missing wall/window sections for guests. Shop expansions were only ever applied as one-way animations, so a wall piece lost to an interrupted animation stayed missing. Guests now periodically re-run the game's own wall repaint.

## 1.0.9
**Stable per-box IDs — removals can't destroy the wrong box.**
- Fixed: a box could vanish from a player's hands while restocking. Boxes were matched between players by list position, so any removal shifted every later index and the reconcile rebuilt the wrong boxes — including a carried one. Every box now has a permanent ID; removals are surgical.
- Hardened the 1.0.8 rejoin protection: the anti-wipe guard now also covers card boxes and furniture boxes, is per-player, and closes a timing race that could disarm it during a quick reconnect.

## 1.0.8
**Guest rejoin no longer wipes the host's boxes.**
- Fixed the first major field bug: when a guest re-joined, their world-reload destroyed their local boxes, and the mod forwarded all ~250 as "player trashed a box," deleting the host's entire box population ("boxes disappeared from shelves and appeared outside the shop"). Fixed with a reload suppression window plus a host-side flood guard that also protects against guests still on 1.0.7.
- Fixed: customers blinking in and out on rough connections (despawn timeout raised from 1.5s to 6s).
- Fixed: false "product catalogs differ" warning at join from blank placeholder entries and mod-load timing; both players now get an explicit all-clear.
- Guests can now serve the register even when a worker is manning it.

## 1.0.7
**Public launch + auto-updating launcher.**
- First public release on GitHub and Nexus, with a Discord community and a polished WPF launcher that auto-updates the DLL and launches the game.
- Folded together the pre-launch work: identity-keyed orders and shared licenses, native trade-screen serving, enum/card-database auto-sync, furniture-box sync, settled-physics boxes, and 3-player echo fan-out.

## 1.0.6
**3-player fixes.**
- Echo fan-out so a host applying one client's action reaches the other clients; relayed player names; instant box pickup/set-down visibility; register-reach distance corrected.

## 1.0.5
**Carried-box echo fix.**
- The joiner no longer echoes stale state for boxes the host is currently carrying.

## 1.0.4
**Sky drift heal.**
- Joiner sky/time drift corrected with a periodic heal heartbeat on the lighting sync.

## 1.0.3
**Carried-box pose.**
- A carried box now sits in the avatar's arms instead of down at the knees.

## 1.0.2
**Catalog-check timing.**
- The join-time catalog check now waits for late-registering content mods before warning.

## 1.0.1
**Toast wrapping.**
- On-screen notifications wrap instead of clipping off the edge.

## 1.0.0
**Initial public release.**
- One shared shop over Steam lobbies or LAN: money, XP, collection (graded cards included), item and card prices, shelf stock, card walls, loose boxes, placed furniture, licenses, bills, room expansions, tournaments, grading, and the daily market. Both players work the register, run trades/sell-ins, restock, price, order, hire, and pay bills. Mod-stack aware (PTCGO / Enhanced Prefab Loader), host-authoritative, joiners never write their own saves.
