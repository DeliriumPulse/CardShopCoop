# CardShopCoop — Changelog

True co-op multiplayer for TCG Card Shop Simulator. Both players must run the
**same version** — the join handshake enforces it.

---

## 1.2.0
**Boxes and furniture now stay in sync through pickups, throws, placement, and joining, and cards no longer vanish when a guest sets them out to sell or is holding them.**

- **Fixed: a shelf placed during the session wouldn't let the other player stock it.** A brand-new
  shelf hadn't worked out how many items fit yet, so the game treated it as full for the other
  player only — your item jumped straight back into your hand while the shelf showed the product's
  label with nothing underneath it. The host now works out the shelf's real capacity when the first
  item arrives, so either player can stock a new shelf immediately, and a shelf that genuinely can't
  fit the item still refuses it cleanly.
- **Fixed: removing a label from a warehouse rack didn't update the other player.** Right-clicking a
  warehouse rack's tag cleared the label on your screen but the other player's rack kept it. Warehouse
  rack labels now sync like ordinary shelf labels, without touching the boxes stored on the rack.
- **Fixed: an emptied box that still showed its label wouldn't accept a different product — the item
  jumped back into your hand and could make the guest stutter.** The game remembers a box's label after
  the last item is taken out, so the co-op side read the empty box as "still holding" the old product
  and kept refusing the new one, bouncing it back and forth on every attempt. An empty box (and an
  empty shelf) is now treated as able to take any product again, matching the game's own behavior, so
  restocking an emptied labeled box works and the repeated round-trips stop.

- **Fixed: a guest manning a register could get stuck after a sale — the money drawer stayed
  open, the received/total/change screen never cleared, and the guest could not interact or
  open the pause menu.** The guest now runs the game's own register completion, the host
  validates every action and asks the guest to reset if the two disagree, and the drawer,
  payment screen and card reader are released properly.
- **Fixed: money a player had already taken out during checkout was invisible to a client who
  took over that register, and the finish could be refused.** Every change click is now
  mirrored to the other player and the live change state rides the register snapshot, so the
  money on the table and the change total match for everyone.
- Fixed: a player standing near another player's register could scan items or take change there.
- Fixed: a register could be left with frozen coins or a floating card reader after a sale.
- **Fixed: a customer who had just been served could vanish for a few seconds on the other
  player's screen and then reappear.** The customer's mirrored body is now kept ready while the
  register is using it and handed back the moment the sale ends, instead of being rebuilt from
  scratch.
- **Fixed: a player joining during a checkout saw the change total but not the bills and coins
  already on the table, and could not take change back with right-click.** The host now
  replays the checkout's money clicks to the joining player, so the table and the change state
  look the same to everyone and taking change back works.

- **Fixed: a card a guest placed on a display could be deleted by a later sync instead of
  returning to the binder.** A card leaves your collection the moment you pick it up, so until
  the host confirms it is on the display, the card in your hand or on the shelf is the only copy.
  The display sync used to trust any snapshot that said the slot was empty and would destroy that
  copy. Placement now waits for an explicit answer from the host: if the host has the card the
  slot is confirmed, and if the host could not accept the placement the card is returned to the
  binder instead of disappearing.
- **Fixed: opening a graded-card return box while already holding cards could lose cards.** The
  game's box open adds every returned card to your hand without checking the hand limit, which
  could overflow it or throw partway through. Any card that does not fit is now put safely into
  the shared binder (and mirrors to the other player) rather than being dropped or left in a box
  that then despawns.
- **Fixed: a saved hand that already overflowed could silently lose the extra cards on load.**
  Cards beyond the hand limit are now returned to the binder before the game trims the list.
- Joining players now see market and price values re-sync after the world finishes loading,
  instead of briefly seeing stale values.

- Guests can sell boxed furniture, and the host removes the same authoritative furniture for both
  players.
- Guests can box up placed furniture without creating a guest-only copy; the host owns that box
  lifecycle too. Boxes opened for furniture placement stay hidden from other players until
  placement is complete, and a thrown furniture box no longer reappears with its lid open.
- Boxes held by one player are hidden and protected from pickup by everyone else, and box ownership
  and movement travel as a single authoritative value instead of several separate flags that could
  disagree. This fixes rare cases where a box could stay invisible, remain stuck in move mode,
  appear owned by the wrong player after a throw, a drop, or a disconnect, or fail to sync at all
  for a guest joining a shop that is already open; box updates are now always applied in order, so
  a catch-up after a stall can't silently drop a box's last move or removal.
- Box pickup, drop, and furniture box-up changes reach the other player immediately instead of
  waiting for the polling interval, so remote players see furniture boxes disappear as soon as
  someone picks them up, and throws reproduce the game's launch impulse on the receiving side
  instead of dropping at the thrower's feet.
- When one player pushes a box, the other player's copy now slides along in real time instead of
  jumping ahead, and the box settles back into normal physics as soon as the push ends.
- Item boxes placed with Q can be picked up normally by the other player after they are set down,
  and Q-mode box movement follows the holder's camera smoothly on both sides.
- Opening or closing an item box updates on the other player's screen too, including boxes that
  are already open when the other player joins, and delivery-box contents stay synchronized when
  either player restocks from a box or moves items into one.
- Opening or taking items from a loose box no longer lets a delayed update restore items the other
  player already took; the count stays consistent regardless of who edits first.
- Furniture placement previews show the furniture being placed instead of a stray delivery box,
  match the game's normal placement preview (alignment, multi-part rendering, layers, and
  transparency), and no longer rescan the whole shop or leak temporary materials on every move.
- Invalid, stale, busy, or last-cashier-counter sale requests are rejected without creating money.
- Newly purchased furniture now appears on clients immediately when the host places it for the
  first time, instead of appearing only after the host moves it again.
- New characters no longer appear nude, and the game's own starting outfit is kept; an unsaved or
  nude model is repaired to a clothed preset when NSFW is off. A new "Allow NSFW" toggle in the
  co-op window's SETTINGS tab controls whether nude appearances are allowed at all.
- When the NSFW toggle is off, any bare wardrobe slot on another player is filled with that
  slot's default item instead of their whole outfit being randomized, and the Nude wardrobe
  option is hidden so you cannot accidentally make your own character nude.
- A delivery box a worker is carrying now stays in the worker's hands on the other player's
  screen instead of being dragged along the floor, and it returns to a normal loose box when the
  worker sets it down.
- Buying several pieces of furniture no longer spawns extra delivery boxes; each purchase now
  shows exactly one.
- Opening and placing a furniture box is more reliable: a rare case where the placement could get
  stuck while the box stayed hidden, or leave a duplicate box behind, is fixed.
- Giving a worker a bonus now updates the bonus button and amount immediately for the player who
  pressed it.
- Items taken off a shelf now reach the other player immediately instead of waiting for the next
  scheduled sync.
- The co-op window has a new SETTINGS tab with switches for the diagnostic logs (box sync and
  per-frame timing), which save to the config file and take effect right away.
- Fixed an unreadable header in the character panel: "MY CHARACTER" and the other info labels
  were dark text on a dark background.
- When a play-table game finishes, guests now see the green "+$" payment popup above the
  customer who earned it, including when the guest kicks customers off a table, instead of the
  popup appearing only for the host.
- The guest's trade and sell-in screen now follows the same flow as single-player. Pressing
  Accept no longer snaps the window shut: the game waits for the host's answer and then shows
  the customer's real reaction - the accepted message with a Done button, or the haggling /
  refusal line with the new asking price - so a guest can accept, counter-offer, or walk away
  exactly like the host can. Money and cards still change once, on the host. If a player
  disconnects mid-trade, the host immediately releases that customer and the guest's screen is
  closed cleanly instead of being left open.
- **Fixed: an item you picked up could be placed or opened before the host confirmed the pickup,
  and a delayed or rejected transfer could leave two copies of the same item.** A pickup is now
  held and protected from the moment you take it, and an answer the game is slow to deliver is
  retried until the host confirms it instead of being treated as accepted after a timeout. This
  covers taking an item off a shelf and immediately using it, and adding or removing items while
  the other player is editing the same box.
- **Fixed: opening or closing a loose box without picking it up did not update the lid on the
  other player's screen.** A guest can now open, close, add to, and take from a loose box, and the
  lid and contents stay consistent for both players.
- **Fixed: after stopping and starting a session, hiring or interacting with a worker and changing
  shop settings (decorations, equipment, game-event fees, cashiers, and table numbers) could
  silently stop working.** The affected systems now reconnect on every session.
- **Fixed: a card placed on a display that the host never confirmed could stay on the shelf
  forever without a word.** The card is now kept safely on the display and you are told it is still
  waiting for the host, instead of the shelf quietly disagreeing; if the host explicitly rejects
  the placement, the card is returned to the binder.
- **Fixed: opening a graded-card return box could fail with a misleading "save drift" message.**
  Failures now explain the real reason, including when the other player is holding the box or is
  missing a card content pack.
- The SETTINGS tab gained **latency testing** sliders that add artificial delay and jitter to
  incoming network traffic, so connection problems can be reproduced on purpose.
- **Item transfers are now confirmed by a single answer from the host.** A pickup or placement is
  retried until the host's answer actually arrives, but the host applies it only once and gives a
  single deciding answer: it either goes through or the item is returned exactly once, so a slow
  connection cannot apply a transfer twice or leave an item caught between the two players.

Both players must update.

---

## 1.1.0
**A safer, fuller co-op session with the game's real register and a more reliable network.**
Both players must update.

- The register now uses the game's vanilla interaction flow for joiners: man the counter,
  click items or cards to scan, take payment, give change, and press Space to finish. The old
  quick-serve key was removed so the guest sees and uses the same checkout screen as the host.
- Added shared register carts, TV state, purchase results, player models, movement previews,
  NPC speech, and atomic purchase handling for a more complete shared shop.
- Fixed join-world loading so synchronization stays paused until the newly received shop has
  actually finished loading, including slow save and mod-data writes.
- Fixed stale join work after disconnects, simultaneous register claims, and retained customer
  mirrors being reused by a later checkout.
- Replaced the old binary payload plumbing with validated JSON messages and centralized routing.
  Reliable state and transient movement traffic remain separate, and malformed payloads are now
  logged and discarded without taking down the connection.
- Fixed stale sell-in price fields carrying a previous customer's bid into the next customer.
- Disconnected players no longer leave pack openers claimed or item boxes locked indefinitely.
- Relayed player names are restored after a scene load, including Steam display-name resolution.
- Restored the project checklist and audit notes so the tested game systems remain documented.

This release is **Meepen**'s work — the vanilla register flow for joiners, the new message layer, and the build/CI setup. Thank you!

---

## 1.0.44
**Fixes $0.00 card prices on the joining player, and the empty collection binder and bulk-box lists that come with them.** Mostly hit Game Pass players. Both players must update.

**If your cards look like they vanished, they didn't**
- Nothing was deleted. Every card is still in your collection — the binder just stopped being able to sort them.
- The binder sorts by **price** by default. When every card reads $0.00 there is nothing to sort by, so instead of your collection being grouped together on the first few pages it gets spread one card at a time across every page, with empty slots in between. It looks exactly like most of your collection is gone.
- **Switch the binder sort from Price to Amount and they all come straight back.** That works even without this update. Avoid *Duplicate Price* and *Total Value* — those read the price too, so they look just as broken.

**What was actually wrong**
- When you join, the host's card prices are supposed to arrive with their world. On the Game Pass build they weren't arriving, and the base price for every card stayed at zero.
- The game repairs that by itself a moment after loading — it fills in any card price still sitting at zero. **My mod blocked that repair on the joining player.** For a good reason: left alone, your copy would invent its own prices and drift away from the host's. But I blocked the repair without ever sending the real prices over the network, so there was nothing left to fix it. It stayed at $0.00 for the rest of the session, and every session after.
- Card prices now travel between players, the same way item prices already did. And the repair is only blocked when the host's prices actually arrived — if they didn't, your copy fills them in and then gets corrected by the host a moment later.

**Why the bulk boxes were empty too**
- The workbench and the bulk donation box hide any card worth less than $0.01, and the minimum can't be dragged below a penny. With every card at $0.00 that filter removed all of them before it even looked at how many you had. Fixed by the same change.

Both players must update — the launcher does it automatically.

Thanks to **ItsYourBoyBlu** and **wd-40** for the reports and logs.

---

## 1.0.43
**Fixes a bug I introduced in 1.0.28 that permanently destroyed Grading Overhaul grades. If you use Grading Overhaul, please read the recovery section below before you play again.** Both players must update.

**What it did**
- The binder's "total value" number is refreshed live while you play, so it keeps up as cards arrive and leave. To calculate it, my code copied what the base game does — including a line that says *"any grade above 10, set it to 10."* Grading Overhaul stores the grading company, the real grade and the certificate number packed into that single value, so that line flattened all three into a plain grade 10. Permanently, for **every graded card in your album**, written straight to your save.
- It fired whenever a card moved between the two of you **while the graded card album was open** — so it could run many times in a session, on either player's PC. The base game only does this once when you open the binder, and Grading Overhaul ships a companion mod (GradeDataLifeSaver) that stops it there. That mod could never stop mine, because it patches the game and my copy lives inside CardShopCoop.
- Nothing writes to your graded album from this code any more. The total is now calculated from a throwaway copy, and it also stopped under-reporting the value of graded cards by ignoring Grading Overhaul's company multipliers.

**If your graded cards already turned into plain grade-10 slabs**
- **Your cards are not ruined.** They are ordinary grade-10 cards now — the top of the base game's scale — and **you can submit them for grading again** to get real certificates and slabs back.
- **Only the album was affected.** Graded cards on shelves, in your hand, in package boxes, or away at the grading company kept everything.
- **Check your partner's copy first.** This only ever damaged the PC whose graded album was open. If only one of you was in the album, the other's cards are intact, and the **Adopt graded cards** button in the co-op panel restores them properly — grades and all.
- **Check for an older save**, in `%USERPROFILE%\AppData\LocalLow\OPNeonGames\Card Shop Simulator\`: `savedGames_Release1/2/3.json` are one-time snapshots the game makes into free slots, and `savedGames_ReleaseBackupFile0.json` is the previous save — **that one is overwritten by the next autosave, so copy it somewhere safe now** if you want it.
- **The grades themselves cannot be recovered from Grading Overhaul's files.** It records which certificate belongs to which card, but it has never stored the grade. Be careful of any tool claiming it can repair this: assigning a certificate to the wrong copy makes Grading Overhaul mark the card **fake** — 1% of its value, permanently, and a fake card can't be regraded. That is worse than leaving it as a grade 10.

**Two smaller fixes**
- **Fixed: abandoned grading slots showing up as phantom certificates.** Backing out of the grading screen leaves the slot holding a certificate with no card attached, and the album check counted it — then reported a "certificate collision" against the real card holding that certificate.
- **Fixed: certificate-collision warnings printing the same card name twice.** The message compared the expansion, card, border, foil and dimension version, but only printed the expansion and card — so a genuine clash between, say, a foil and a non-foil copy read as nonsense. It now spells out what actually differs.

Both players must update — the launcher does it automatically.

## 1.0.42
**Fixes a bug I introduced in 1.0.41 — the graded-album drift warning was mostly false alarms, and the repair button could make things worse. Thanks cavi for the report that exposed it.** Both players must update.

**The drift warning kept coming back**
- **Fixed: the album check counting cards that were simply in someone's hand.** A graded card you're holding — just picked up, or just taken out of a returned package — was counted as "in your album" on your PC while your partner's copy had correctly already been removed. So every single graded card either of you touched got reported as drift. The check now only looks at places both games actually share, so cards in motion no longer raise an alarm.
- **Fixed: the warning never going away once it appeared.** The "all clear" reply was only sent when something *did* differ, so a ten-second hand-hold left a permanent button on screen. Both sides now confirm a match, and the alert clears itself.
- **Fixed: shelved graded cards drifting depending on when each of you last opened a menu.** The check was reading a snapshot of your shelves that each PC refreshes at different moments; it now reads the live shelves.

**⚠️ If you used the "Adopt" button in 1.0.41**
- Pressing it while a card was still in someone's hand could add a *second* copy of that card — and when the holder put the original away, Grading Overhaul saw the same certificate twice and marked both as fake. That window is closed in 1.0.42, but if you have graded cards showing as fake that shouldn't be, that's where they came from. Sorry — that one's on me.

**A real one, found while investigating**
- **Fixed: the joiner's game secretly grading its own cards every in-game day.** Grading Overhaul's day-end grading was running ahead of the mod's block on the joiner, minting its own certificate numbers from the joiner's counter. That's very likely the original source of mismatched certificates between players. The host is now the only machine that issues certificate numbers; the joiner adopts the host's exactly.
- **Also fixed, and it dates back to 1.0.40:** the joiner could forward a grading submission that Grading Overhaul had already rejected (mixed companies, fake cards, already-regraded). The mod was relying on patch ordering to give GO's checks the final say, and the mod loader this game uses doesn't work that way — every check runs regardless of order, so GO's "no" was being discarded. The joiner's submissions now ask Grading Overhaul directly and stop when it says no, using GO's own rules and its own error message.

**Requested**
- **New setting: quiet the on-screen drift alert.** The popup and banner can be set to show always, once per session, or never. The log line always prints regardless, so a bug report still has everything in it.
- Auto-adopt was requested too and is deliberately *not* here: with the false alarms gone, drift stops recurring, so there's nothing to automate — and automating the button would mean pressing it during exactly the moments that caused the duplicate problem above.

## 1.0.41
**Graded-card drift: detect it, stop it growing, and offer a real repair — thanks cavi, whose two-sided logs and their own analysis of the remove/re-add pattern cracked a genuinely deep one.** Both players must update.

**Graded cards appearing out of nowhere / vanishing on one side**
- **Fixed: moving graded cards around (staging them for grading, picking them up, putting them back) could make copies materialize on the other player's screen.** When your albums had quietly drifted apart, a "move" arrived as just the "put back" half — manufacturing a card the other side never had, and in the worst case tripping Grading Overhaul's anti-cheat into flagging REAL cards as fakes. A move is now treated as a move: if the "take out" half didn't apply, the "put back" half is skipped too (and still passed along to other players who do have the card).
- **New: the mod now compares graded albums between players and tells you when they've drifted** — a warning names the exact cards each side is missing (checking albums, shelves, hands, boxes, and cards away for grading, so it doesn't cry wolf about cards that are just in motion).
- **New: a repair button.** When drift is detected, the co-op window offers "Adopt N graded cards \<player\> has that you don't" — one click, one direction, adds only, never deletes, and refuses any card whose certificate number already exists on this PC in any form (Grading Overhaul's anti-cheat matches on the certificate alone, so that's the only safe rule). Nothing is ever repaired automatically, and the button's count is exactly what it will add.
- Cards whose certificate exists on both PCs but got rewritten by the anti-cheat on one side are reported as their own "re-encoded" category — the clearest sign of past drift — and are never offered for adoption.
- Certificate collisions (same cert number on two different cards, one per PC) are reported loudly as their own category with no auto-repair — merging those is exactly what turns real cards into fakes.
- The log now says plainly when joining replaced this slot's Grading Overhaul data with the host's copy (solo slots are never touched).

**Deodorant machine crash on the joiner (the "kind 10" error)**
- **Fixed: the joiner's auto cleanser machine erroring forever after a join.** The machine's item counter got inflated during the world download and every sync after that tripped over it — and the periodic re-send just reproduced the error each time. The counter now stays in step, and an already-broken machine self-heals within a couple of syncs.

**Warehouse racks**
- **Fixed: rack slots being permanently lost to "ghost" boxes.** A destroyed box could leave its slot occupied forever, forcing every later box to be pinned in place instead of stored properly — and each pin made it worse. Dead slots are now reclaimed on the spot.

## 1.0.40
**Grading Overhaul submissions from the joiner — thanks cavi, whose "charged the company price, got a vanilla slab" detail pointed straight at the seam.** Both players must update.

**Joiner grading submissions (Grading Overhaul)**
- **Fixed: the joiner's grading submissions coming back vanilla-graded (no company slab, no certificate) despite paying the company's price.** Grading runs on the host's game, and the "which company" part of the submission never made the trip — the host filed it as a plain vanilla submission. The submission now carries the company across, and the host enrolls it exactly the way Grading Overhaul itself would: same encoding, same pre-rolled grades, same certificates. What you paid for is what comes back.
- **Also fixed: the joiner's grading app showing wrong tiers/days for the HOST's company submissions.** A number that carries Grading Overhaul's company info was being squeezed through a field too small for it on the way to the joiner. This was broken independently of the submission bug.
- One deliberate rule: whether "cheat mode" grading odds apply is decided by the HOST's Grading Overhaul settings, not the joiner's — a joiner can't force fake cards into the shared album.
- Money note: you were never double-charged — the shared wallet paid once. The bug was paying premium price for a vanilla product; now the product matches the price.

**Small stuff**
- **Fixed a card-loss trap: the joiner submitting more than 8 cards for grading** (Grading Overhaul expands the screen to 52 slots) **silently lost the extra cards.** The co-op wire now carries up to 52 when Grading Overhaul is installed, and if a submission ever can't be forwarded whole, it's aborted with your cards left safely in the binder instead of being trimmed.
- The mod's grading-submission hook now explicitly runs AFTER Grading Overhaul's own validations, so a joiner can't accidentally submit a combination GO would have rejected (mixed companies, already-regraded cards).
- When the router declines automatic port forwarding, the host panel now also suggests the practical fix: have the other player host.

## 1.0.39
**Hotfix: Game Pass hosting broken in 1.0.38 — thanks Tonio's crew, whose logs from seven join attempts told the whole story in one read.** Both players must update.

**"Host snapshot failed" on Game Pass**
- **Fixed: the mod deciding you were on Steam because a Steam file exists on your PC.** It turns out the Game Pass build itself ships a hollowed-out copy of Steam's networking library (with most of its insides stripped out), and other mods sometimes bundle a full copy — either way the old check saw the file and demanded a save file Game Pass never creates. The mod now ignores that file entirely for saves and instead asks the game directly whether the save it just requested actually completed, using the game's own save counter — which works identically on both builds.
- **Bonus fix for Steam:** if the game's save succeeds but its file write fails (locked file, full disk), hosting now ships the confirmed-current world from memory instead of erroring out.
- The joiner-side world delivery no longer depends on platform detection at all.
- If a full copy of Steam's library sneaks onto a Game Pass install (bundled by another mod), the Steam buttons no longer appear as dead controls — the mod now tests whether Steam can actually run before offering any of it.
- Logs now state plainly which save backend is in use and, when a world is sent from memory, which world it was (day and shop name) — so the next report answers itself.

## 1.0.38
**One mod for everyone: the same download now works on Steam AND Xbox Game Pass — huge thanks to Jburne10, whose Game Pass port supplied the save-transfer fix and the proof it works.** Both players must update — Steam players via the launcher as usual, Game Pass players by dropping the same DLL into `BepInEx\plugins`.

**Game Pass support (new)**
- **The mod now loads and runs on the Microsoft Store / Game Pass build.** Previously it didn't just fail there — it left half-installed hooks that broke saving and customers. Now it detects that Steam isn't part of that build and cleanly runs in LAN / direct-IP mode.
- **Fixed: hosting on Game Pass never delivered the world to the joiner.** Game Pass saves live in Xbox storage containers the game's own process can't even see, so the old file-based world snapshot could never find them. On Game Pass the world is now read straight from the game's memory (Jburne10's approach) — no files involved. On Steam the slot file stays the only accepted source: if the game skips the save you get a clear error rather than a stale world.
- Game Pass play is LAN / direct IP: same house just works; over the internet the host port-forwards and the joiner enters the host's public IP. The co-op window shows exactly the options your build supports — no dead Steam buttons.
- **New safety gate: the game versions must match.** The Game Pass build can run a different game version than Steam; the join handshake now compares them and refuses a mismatched pair with a clear message instead of letting two different game builds corrupt each other. (A host who understands the risk can set `AllowCrossBuildJoin` in the config for supervised cross-play testing.)

**Invite codes + automatic port forwarding (new, for LAN / direct-IP play)**
- **Hosting now gives you a "Copy invite code" button** — one short code with your address, port, and a randomly generated session password baked in. Your friend pastes it into the new "Join by code" field and they're in. No more reading IP addresses over voice chat.
- **LAN hosting now has a session password.** A random password is generated each time you host (shown in the host panel for friends who type the IP by hand, and carried inside the invite code automatically), so an internet-reachable port is never an open door. Configurable (`AutoLanPassword`, on by default).
- **The mod now asks your router to forward the co-op port automatically (UPnP)** while you host, and removes the mapping when you stop. Works on most home routers; when the router declines, the host panel says so and the manual port-forward instructions still apply. Configurable (`AutoPortForward`, on by default).
- Steam sessions are untouched by all of this — Steam invites already cover it. (The "Join by code" field works on Steam builds too, so a Steam player can join a Game Pass host's code over IP.)

**For Steam players**
- Nothing changes: Steam lobbies, invites, and the launcher all work exactly as before. This release was verified against the Steam build line-by-line; Game Pass support is marked experimental until it's been through more field time.

## 1.0.37
**Hotfix: modded cards missing from the shared album — thanks Aetheryk and the_capyman, and especially the paired host+guest logs, which proved it in one read.** Both players must update — the launcher does it automatically.

**Pokemon (and other content-pack) cards not appearing in your partner's album**
- **Fixed: cards your partner pulled being wrongly refused as "from a card set you don't have installed" — even though you both have everything.** Since 1.0.34, the safety check that protects your album from genuinely-unknown cards asked the wrong authority: it checked the game's enum table, but content packs register their monsters as DATA, numbered 1..N per expansion — so every card past #122 in a modded set failed the check on every PC, universally, while cards #1–122 slipped through by accidental number collision with vanilla monsters (that split is why "some" cards synced and some didn't). The check now asks the same per-expansion card list the game itself uses, so data-backed modded cards pass and genuinely-missing content is still refused.
- Also hardened: card REMOVES ran no check at all and could crash outright for modded cards; two more paths (grading returns, card-box collection) applied wire cards unguarded; and the check could no longer be tricked into permanently breaking the game's item database during a loading screen.
- Better logs: refused and applied modded cards now print as Expansion#N instead of a bare number (which could render as an unrelated vanilla monster's name), refusals log once per card instead of hundreds of times, and a card that genuinely can't be processed on one PC now says so instead of vanishing silently.
- In a 3+ player session, the host now passes along card changes for content packs it doesn't have installed itself, so two guests who share a pack stay in sync even when the host lacks it.
- Your partner's cards were never deleted — they always existed on their side. After both of you update, newly pulled cards sync normally, and older stuck cards can be nudged across by having their owner place one on the card table and pick it back up (this transfers exactly one copy per card — repeating it won't duplicate).

## 1.0.36
**The join-rejection fixes — thanks Curi Cole's screenshot and the pair who tested on a pure vanilla game (that report cracked it).** Wire format extended, so 1.0.36 only connects to 1.0.36. Both players must update — the launcher does it automatically.

**"Your card database still conflicts after syncing" (the big one)**
- **We were wrong about how the content-pack loader works, and the fix follows from it.** That error told you copying the host's database can't work and that you need identical content packs. Neither is true: the loader KEEPS every ID already in the database file — it only invents fresh IDs for genuinely new content. What actually differs between two PCs with the same packs is just the ORDER the IDs were handed out (install order). So the database sync does fix it — the message now says so, the sync is attempted twice per session instead of once, and if it still fails the error tells you the actual reason (didn't fully quit to desktop, the host needs a restart, or where to hand-copy the file).
- **Fixed: a host who had ever JOINED someone couldn't auto-sync anyone afterward.** An internal "my database is borrowed" flag never cleared and made the host refuse to send its database — forever. It now only blocks when the database file genuinely doesn't match what the host is running (i.e., an actual restart is owed).

**"Same error on a pure vanilla game"**
- **Fixed: leftover files from uninstalled mods haunting the connection check.** Uninstalling your content mods doesn't delete the ID database (it lives outside the game folder), and the check would fall back to reading that stale file — so two vanilla players could conflict forever over Pokemon content neither of them still had. A game with no content loader loaded now reports a clean empty state, always.

**Under the hood**
- Groundwork for name-based ID translation: the host's registry now travels in the join handshake, and every modded ID that crosses the wire goes through a translation layer. With matching registries it changes nothing (identity, verified). The one place it's already live: content only ONE of you has installed now crosses the wire as an explicit "not installed here" marker instead of a raw foreign number — which closes a few places where a foreign ID could accidentally collide with something you DO have. Full translation (no more restart-after-sync) activates in a future version.

## 1.0.35
**The binder freeze and the price fixes — thanks to the anonymous July-27 reporter running the Pokemon pack-opener setup.** Wire format extended (card changes now travel in batches), so 1.0.35 only connects to 1.0.35. Both players must update — the launcher does it automatically.

**The 20–30 second freeze with the binder open**
- **Fixed: your game freezing while the other player opens packs or collects card machines with the binder open.** Every single card they gained used to trigger a full re-sort and rebuild of your open binder — hundreds of times for one "collect all" click. The binder now updates once per frame no matter how many cards arrive, card changes travel in one batched message per frame instead of one message per card, and a backlog is processed on a per-frame budget instead of all at once. The freeze is gone; you just see your binder totals tick up.

**Co-op player's card prices (both directions)**
- **Fixed: the joiner's card prices not sticking / never reaching the host.** A price you set was sent exactly once with no confirmation — if that one message got squeezed out (which the pack-opening flood above did reliably), the host never saw it, and the host's periodic price re-sync then painted its own OLD price right back over yours. That's why it felt like you "couldn't set prices at all." Now the joiner re-sends until the host confirms, the host's re-sync can't overwrite a price you just set, the host confirms every applied price back (which also finally delivers your prices to a third player), and mismatched currency settings between PCs no longer make confirmations impossible.
- The host now says so in the log when it *can't* store a price (card set not installed, or a modded price store rejecting the write) instead of failing silently — and when the host can't store it but the other players can, it passes the price along anyway.
- Item prices got the same safety window (the host's bulk price table can no longer repaint an item price you set seconds ago), and a rejected item price now shows a message on your screen instead of silently reverting later.

**Under the hood (co-op safety)**
- A stale session's not-yet-applied card changes can no longer replay into your NEXT session's collection (they're now cleared on disconnect).
- One corrupt card change in a batch now costs exactly that one card, not the whole batch.
- A declined purchase can no longer slip through as free product if the charge and the delivery get split across a message backlog.

## 1.0.34
**Fixes for the latest report batch — thanks Metz, Domworth, Toat, and Skibbles (whose repro notes basically drew the map).** Wire format extended, so 1.0.34 only connects to 1.0.34. Both players must update — the launcher does it automatically.

**The recap screen (Metz)**
- **Fixed: the joiner getting stuck in the end-of-day recap.** The recap's Next Day button silently did nothing for guests, the only exit was an invisible click-anywhere, and once stuck, every later night's recap was swallowed too. Now the button closes it, the host advancing the day closes it automatically, and your movement unlocks properly.

**The "custom card database" loop, part 2 (Toat)**
- **Fixed: the database-differs loop when your PLUGINS match but your CONTENT packs don't.** The game rebuilds its card-ID registry from YOUR installed content on every boot, so copying the host's database could never stick when content differed — infinite sync-restart-reject. The check now compares the IDs your game is ACTUALLY running (not a file), only rejects on a genuine conflict (same card name mapped to different IDs), and allows one-sided extras — they just show the existing "catalogs differ" heads-up. If a true conflict can't be fixed by syncing, you now get one honest message telling you to match content packs instead of an endless loop. Bonus: far fewer database syncs = far fewer "solo saves won't load" incidents.
- Safety net that made this possible: a card from a set you don't have installed is now skipped with a clear log line — previously it silently credited your first Tetramon card.

**Items vanishing from shelves (Domworth)**
- **Fixed three ways items could vanish into the void**: the periodic stock heal could roll back items a guest had *just* placed; a shelf your game couldn't render correctly (content mismatch, capacity differences) echoed its shortfall back and wiped the HOST's shelf too; and an item type from a content pack you don't have was zeroed instead of skipped. All three closed — worst case now is "he sees items I don't" instead of items being destroyed for both of you.

**Box duplication & warping (Skibbles)**
- **Fixed: items pulled out of a box teleporting back in** (duplication) and **boxes the host places or throws warping away**. Root cause was one mechanism: the guest's mirror could get stuck endlessly re-reporting stale box contents and positions it never touched. Guests now only affect a box's contents while they're actually holding it (or just set it down), and the stale-echo latch is gone — which also fixes the reverse case where items a guest unloaded stayed in the box.

## 1.0.33
**Hotfix: the endless "custom-card database differed - RESTART" loop (thanks joshepi89).** The database check compared the registry file byte-for-byte - but the prefab loader rewrites that file on every game boot, so two players whose card databases genuinely MATCHED could mismatch forever (sync, restart, rejoin, rejected again - no number of restarts escaped it). The check now compares what the registry actually MEANS (every card/item name and its ID), so identical databases match on the first rejoin, and the "already synced" message can no longer appear in a loop. If the databases genuinely differ, sync + one restart still fixes it as designed. Both players must update - the launcher does it automatically.

## 1.0.32
**Hotfix: the guest's serve key going permanently dead ("guest can't interact with npc" - thanks Coke).** A safety guard added in 1.0.29 (don't fire the serve key while typing in a text box) checked whether a game text field was *selected* rather than *actively being edited* - and Unity keeps the last-clicked UI element selected forever, so touching any price box / phone app / website input silently killed the serve key for the rest of the session. It now only suppresses while you are genuinely typing, the co-op window can no longer leave a stale focus behind when closed, and a suppressed press is logged instead of silent - so if a key ever dies again, the log says exactly why. Both players must update - the launcher does it automatically.

## 1.0.31
**New in-game look.** The co-op window (F2) got a full visual overhaul - a proper themed panel matching the game's cozy style (rounded corners, title bar, HOST/JOIN section cards, colored status chips, styled buttons and fields), and the on-screen prompts (serve hints, host clock, errors) are now readable dark pills instead of floating text. No gameplay or sync changes. Both players must update - the launcher does it automatically.

## 1.0.30
**Fixes for the full 1.0.29 field-report batch — thank you Bytelocker, SerDakota, dereck, Kamun, Elvundil, Latch, and everyone in the Discord.** Wire format changed again, so 1.0.30 only connects to 1.0.30. Both players must update — the launcher does it automatically.

**Your solo saves (the big one)**
- **Fixed: solo modded saves showing "data lost" after playing co-op.** Joining a host with a different custom-card database syncs the host's registry over yours (with a backup) — and there was no way back, so your own modded saves couldn't load anymore. The co-op window now shows a clear warning whenever your registry is the host's copy, with a one-click **"Restore MY card database"** button (restart afterwards). Nothing is ever lost — every sync keeps backups.
- The host's real save slot is no longer overwritten by the join-time snapshot (it saves to a scratch slot now), so a mid-session problem can never be baked into your only copy.

**Joining (fixed a serious wipe)**
- **Fixed: a guest joining mid-day could WIPE everyone's shelf stock** — the fresh guest "reported" every shelf as changed-from-empty and the host applied it. This was also behind "our third player joined and we lost all our stock" and "the shelves were erased after we restarted." Joining is now safe at any time of day, and item stock heals itself every 12 seconds like card displays already did.
- The "mod set differs" rejection now tells you **exactly which mods differ** — missing, extra, and version mismatches by name (same for custom-card differences).

**1.0.29 regressions (Latch was right)**
- **Fixed: guest purchases were delayed ~1.5 seconds** and a purchase the shared wallet couldn't afford still went through. The purchase/charge pairing was rebuilt properly.
- **Fixed: boxes freezing solid in the warehouse** ("clogging up our shelving") — a guest-side fix was wrongly running on the host and pinning real boxes in place.

**Boxes & items**
- **Fixed: opened boxes refilling endlessly** ("unlimited items") — the guest was echoing stale box contents back at the host while it dispensed.
- **Fixed: boxes a guest trashed silently surviving on the host** and "respawning the next day" — an over-eager anti-grief cap was eating legitimate cleanup sprees; it now allows them and logs when it ever declines one.
- **Fixed: boxes the host moves snapping back** to the delivery area or the guest's old placement.
- Boxes can no longer be placed (or reappear) **under the floor**; anything released below the map is lifted back up.
- Staff can no longer take or drain a box out of a guest's hands (carried over from 1.0.29, now with the disconnect cleanup verified).

**Guests**
- **Fixed: clicking near the register soft-locking a guest** into cash-counter mode with no way out (the cause of "they can't interact with anything until they leave"). Guests serve with the serve key (V by default) — the game now says so instead of trapping you.
- **Fixed: the shop name not updating the first time a friend joins.**
- 3-player: furniture can no longer be teleported/vanished by a stale index from another player (moves now verify the object's identity), and fresh joiners no longer re-report every object's position.

**New option**
- `HostServeKey` (off by default): lets the HOST use the quick-serve key at the register too, same as joiners.

## 1.0.29
**Preemptive fixes from a full audit of the game against the mod.** Instead of waiting for field reports, every game system was swept for 2-player coverage gaps (the full audit lives in `docs/audit-2026-07-07.md`: 134 systems confirmed covered, 10 gaps found — all fixed or safely blocked below). Adds one new network message, so 1.0.29 only connects to 1.0.29.

**Duplication & money exploits (never reported — found by the audit)**
- **Fixed: packs sitting in an auto pack opener duplicated on the host every time a guest joined.** The guest's world-load re-sent every stored pack as if a player had just inserted it. Same bug fixed for deodorant cans stored in the auto cleanser.
- **Fixed: a guest could sell a boxed-up shelf the host placed and credit the shared wallet while the furniture survived** — an infinite money printer. Guest furniture *selling* is blocked (with a message) until a fully-validated version ships; host selling is untouched.
- **Fixed: a purchase whose coin charge the host declined (shared wallet short) still delivered the product free** — restock orders, furniture, and licenses now cancel cleanly when the charge is declined.

**Guest actions that silently did nothing (or worse)**
- **Fixed: the guest's handheld deodorant spray never actually cleaned a smelly customer** (it only hit inert local copies). Sprays now forward to the host and land on the real customer.
- **Fixed: a guest placing a decoration lost it** — the host's world wipes the guest's local copy and the inventory count was gone for good; placing one could even teleport a *different* decoration. Deco placement is blocked with a message for now.
- **Fixed: pulling a shelf item into an empty box destroyed the item** (the host rejected the box's new contents while the shelf loss went through).

**Robustness**
- **Fixed: a host worker could take or drain the box a guest was carrying** out of their hands.
- **Fixed: a guest loading their own save from the pause menu mid-session left a half-connected session** — the session now ends cleanly the moment either side loads a different world.
- **Fixed: re-submitting an already-graded card for re-grading could delete a duplicate graded card** on every player who owned one (a stale assumption from 1.0.22 — the same wrong comment caused two other bugs this week; all three paths corrected).

Both players must update — the launcher does it automatically.

## 1.0.28
**Fixes for all 9 bugs from the latest two-player field test.** This build changes the network format (box sync), so 1.0.28 only connects to 1.0.28 — the join screen says so if versions differ.

**Storage & deliveries (the big one)**
- **Fixed: guest-ordered deliveries only showing on the host.** Root cause: the box sync only carried the first **250** boxes, and a well-stocked warehouse blows past that — new delivery boxes were exactly the ones past the cap. Worse, the guest *deleted* its local copies of boxes past the cap, permanently. The cap is now **1000** (the wire count grew from a byte to a ushort), and a capped snapshot can never delete boxes.
- **Fixed: storage boxes floating in the air.** A box the guest's rack refused to store was left loose at rack-slot height with physics on. It now pins in place on the rack, matching the host's view.
- **Fixed: items in storage boxes differing between players.** Stored-box contents could clamp to zero on the guest (adopted boxes had no item-position list); plus the rack rejections themselves now self-heal — a stale "ghost" occupant blocking the slot gets evicted — and every rejection logs full diagnostics (rack indices, occupants, where the host says they live).

**Graded cards & grading (Grading Overhaul)**
- **Fixed: a graded card's price change by the guest not reaching the host.** A patch-ordering race with Grading Overhaul's own price patch made the forward carry a decoded 1–10 grade, so the host filed the price under the wrong key. The forward now always carries the encoded grade.
- **Fixed: the grading bill charging a different amount than the screen showed** (the $200,000 2-day bill that only took $12k). The host was recomputing the fee with the vanilla flat formula; it now charges the guest's actual on-screen total (sanity-checked).
- Grading Overhaul's extra service tiers (beyond the vanilla 4) no longer get truncated to tier 3 on the wire — the enrolled tier, duration, and fee now match what the guest picked.
- A rejected submission (slots full / wallet race) now returns a re-graded card to the **graded album** it came from instead of converting it into an ungraded copy.
- Graded card names in trade offers show the real grade (e.g. "[grade 9]") instead of a ten-digit encoded number.

**Trading counter**
- **Fixed: the trading NPC walking away while the guest's trade screen was still up.** Two halves: the host now pauses that customer's patience timer while a guest has the screen open (per counter — two guests at two counters are both covered), and a trade screen that lost its binding to the counter (the source of silently-eaten Accept clicks and the "wrong card name" popup) now re-binds to the customer you're standing at — at their real asking price — instead of doing nothing.
- Typing a haggle price no longer triggers the serve key (register serve while typing).

**Binder & collection**
- **Fixed: "total value of cards in binder differs between host and guest."** The binder's total-value text only updated when you opened it; it now refreshes live as cards arrive and leave while the binder is open. (Cards bought from NPCs by the guest were arriving all along — this stale total hid them.)
- The guest no longer generates its own card market prices — it always uses the host's, so values can't drift.
- 3+ player sessions: a card gained/lost by one guest now reaches the *other* guests too, and a delta the host refuses (registry mismatch) is never propagated.
- Card removals that would drive a count negative are skipped loudly instead of silently corrupting the collection — and modded-expansion cards (EPL / CardForge packs) are handled safely there.

Both players must update — the launcher does it automatically.

## 1.0.27
**Graded cards now sync (Grading Overhaul).** ⚠️ Please test this one with a real graded card.

The mod was throwing away every graded card. With **Grading Overhaul** installed, a graded card's grade is an *encoded* value (grading company + grade + certificate number), not a plain 1–10 — and an old safety guard treated everything above 10 as "corrupt" and dropped it. That's why graded cards never appeared in the other player's binder, showed only the red "!", or turned "fake" (the game's grading anti-cheat was re-stamping them because they arrived un-registered).

- The guard is gone; graded cards now register properly with Grading Overhaul on arrival (binding the host's certificate), so they appear in the other player's binder with the correct grade and stop churning.
- **Graded-card prices** now sync too (routed through Grading Overhaul's own price store instead of the vanilla one that couldn't hold them).
- Both players still need the **same grading mods** (the join handshake enforces matching mods). Without a grading mod, nothing changes.

This is the first build that actually attempts modded-grade sync, so if a graded card looks wrong, grab a `BepInEx\CardShopCoop_<number>.log` from both PCs. Everything from 1.0.26 is included.

## 1.0.26
**Big field-report batch from live co-op testing** — thanks to everyone reporting in the Discord.

**Placement, boxes & machines**
- **Fixed: placed objects/machines snapping back** to their old spot when the host moved them, and **objects landing a few degrees off the rotation grid** (couldn't align, showed red). Both were the same bug — an object-move tug-of-war where the guest kept re-asserting the old pose. The host is now authoritative over an object it's actively moving, and the guest no longer fights it.
- **Fixed the auto card opener** in co-op: the guest couldn't add packs after collecting once ("no empty slot" — the machine stayed stuck "processing"), and the machine's UI panel floated at the old spot after the host moved it. Both fixed; you no longer have to leave it in one place.
- **Fixed a guest soft-lock:** if a box you were holding got consumed on the host's side, you'd be stuck in "carry" mode with an invisible box — unable to interact with anything, not even the trash. Now it releases carry mode first, plus a safety net that auto-frees anyone already stuck.
- **Fixed furniture placing diagonally** (host and guest): unpacked furniture now keeps its true placed pose instead of the delivery box's random angle.
- **Fixed boxes floating frozen in mid-air** on storage shelves (the mod was reading the wrong rigidbody) and **a box taken off a storage rack** staying stuck on the shelf for the other player.

**Customers, prices & world**
- **Fixed: the host pausing froze everything for the guest.** Pause stops game time, which stopped the co-op tick; the world now keeps running under the pause menu during a session.
- **Fixed: the guest couldn't answer trade / sell-in customers** (the red "!" ones). It worked, but was undiscoverable — clicking the customer does nothing on the guest, and the prompt only showed right at the till. Now a **walk-up hint** tells the guest to go to the counter and press the serve key (V) whenever a customer wants to trade.
- **Fixed: card prices set on a display not showing for the other player** — card prices now re-send periodically so a dropped update self-corrects. (Note: *play tables* can't hold priced cards — use the glass display counter to sell cards.)
- **Fixed grading status showing "-1 day"** remaining for the joiner (clamped to the shared host progress).

**Grading (partial):** guarded a possible crash from modded (un-clamped) grade values. Full graded-card sync with grading mods is still WIP — both players must run the **identical** grading mods, and some graded cards may not yet appear in the other's binder.

Both players must update — the launcher does it automatically.

## 1.0.24
**Fixed: the guest was autosaving the host's world into its own save slot.**
- We claimed a joiner never saves, but a hole let it through: our save guard only blocked saves while you were actively connected (`Role == Client`). After the host left — or the day rolled over, or you quit — you were still *standing in the host's shop* but no longer "a client," so the game's autosave fired and wrote the **host's** world over **your** save slot (the "saves get bundled together" reports).
- The guard now tracks a *borrowed-world* state that's set the moment you join and stays set through a disconnect until you actually return to the **title screen** — so no autosave, day-end save, or quit-save can touch your own saves while you're in someone else's shop. Your solo saves are now genuinely never touched.

Both players must update — the launcher does it automatically.

## 1.0.23
**Tutorial/task progression now syncs to the guest.**
- Previously every tutorial task was host-only: the guest's task panel stayed stuck on "Set the shop sign to OPEN" (and never advanced past any later task) because the actions that credit tasks were forwarded to the host and only advanced the *host's* tutorial. The host now ships its authoritative task progress with the rest of the shop state, and the guest replays it — so both players' task lists advance together.
- Please test this one specifically (it's the first release to sync tutorial state). If a task looks wrong on the guest, a `BepInEx\CardShopCoop_<number>.log` from both PCs helps.
- Also fixed: on a shop with multiple checkout counters, the guest's scanned-item bar could carry over to the next customer (each counter's checkout screen is now cleared on a completed sale, not just one).

Both players must update — the launcher does it automatically.

## 1.0.22
**Big bug-fix pass — a verified sweep of the whole mod. 24 fixes across trades, money, packs, boxes, furniture, customers, world sync, and networking.**

Trades & haggling
- **Fixed the haggle price reading as $0.** When you typed a counter-offer on a sell-in, the game only committed the number when the field lost focus — pressing Accept could beat that, so it sent $0 (or your *last* amount) while the field showed what you typed. The price is now force-committed on Accept and the field is seeded with the asking price, so what you see is what you send.
- **Fixed a traded-in graded card staying in your binder as a "fake."** Graded cards leave the album through a path the shared-binder mirror didn't cover, so a graded card traded/donated/re-graded away on one side ghosted on the other. That removal is now mirrored.
- A one-off pre-roll hiccup no longer marks a customer's offer "host-only" for the rest of their visit (it retries), and the offer you're mid-trade with can no longer time out and close the screen under you.

Money
- **A guest purchase can no longer be silently eaten.** Ordering furniture that isn't in the host's catalog charged the shared wallet and delivered nothing, with no refund — now it refunds (from the host's real price) and says why, exactly like restock orders already did.
- The shared wallet can no longer be overspent negative by a guest buying against a slightly-stale balance — the host now rejects an unaffordable spend and corrects the guest.
- The wallet and shop XP/level/fame now re-send every 15s, so a single dropped economy packet no longer strands the guest's money display.

Packs & binder
- **Fixed the guest freezing on the first card when opening a pack.** A card-mirror hiccup could throw *inside* the game's pack-open loop and wedge the reveal forever; the mirror is now crash-isolated so the reveal always completes.
- A traded/pulled card now appears in an **already-open** binder immediately, instead of only after you flip a page or reopen it.

Customers & workers
- **Customers no longer blink out for the guest** on a brief loading flicker.
- The red **"!" trade prompt** above a customer is now visible on the guest.
- **Female workers** now show as female (they were spawning from the male model).

Boxes, storage & furniture
- **Empty boxes can be taken from the guest again** — the dispenser count no longer strands too-high for ~15s (which made every further click do nothing), and a freshly taken box now appears within a tick instead of up to 1.5s later.
- Loose boxes no longer get randomly scattered to the wrong spot on the guest (the guest's own out-of-bounds sweep is suppressed; the host owns placement).
- Furniture delivered from the generic catalog no longer **duplicates** on every re-sync, and an unresolved generic box no longer **blocks all furniture from being sold/unpacked** on the guest.

World & performance
- **The shop light switch now syncs** — flipping it toggles the light for both players (host-authoritative) instead of only locally, and a light that got out of step self-heals.
- **Decorations now appear for the guest** (they were never syncing).
- The full card-wall repaint now only re-sends when it actually changed (was an unconditional reliable spike every 12s), and a single oversized reliable packet can no longer wedge the whole reliable lane and freeze all state for the guest.
- Population sync no longer silently stops past 250 objects of one kind.

Both players must update — the launcher does it automatically.

## 1.0.21
**Custom cards are now checked at join — no more silent desync from mismatched custom cards.**
- The join handshake already checked mod versions and the modded-item database, but **custom cards added by CreateCards/CardForge weren't covered** (they live in a different ID space that the old checks couldn't see). Two players with different custom cards could connect and then silently see the wrong card. The handshake now also compares the custom-card ID mapping and stops the join with a clear message if they differ, telling you to install the same custom cards (identical files) and restart.
- Harmless if neither player uses custom cards.

Both players must update — the launcher does it automatically.

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
