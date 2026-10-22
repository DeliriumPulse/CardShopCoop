# CardShopCoop

## Unofficial custom build 1.10.0.1

This workspace contains a personal test build based on DeliriumPulse/CardShopCoop
v1.2.0, with fixes for guest joining, character appearance, checkout, and input after
joining. This version number identifies this fork; it is not an upstream release.
The exact changes and validation limits are in [CHANGELOG.md](CHANGELOG.md).
Planned reliability work is tracked in the [multiplayer improvement queue](docs/MULTIPLAYER-QUEUE.md).

### Install on both PCs

1. Close the game. Back up your shop save before the first multiplayer test.
2. Move **every** existing `CardShopCoop.dll` out of `BepInEx/plugins`, including
   nested folders, into a backup folder outside the plugins tree. Leave BepInEx,
   its core files and settings in place. Configuration Manager is optional; this
   fork has its own F2 menu. Both PCs must have matching extra plugins.
3. Extract the package's `BepInEx` folder into the game root beside the game EXE.
   The result should be `BepInEx/plugins/CardShopCoop/CardShopCoop.dll`.
   Do not extract the whole package into the plugins directory.
4. Install the same package on your friend's PC. Both need the same game version,
   other gameplay/content mods, and custom card data. In the game's Settings, select
   the **same currency** on both PCs before joining. Different currencies are rejected
   with a message naming the host's currency.
5. Start the game and check `BepInEx/LogOutput.log` for
   `Loading [CardShopCoop 1.10.0.1]`. There should be only one CardShopCoop DLL.
6. Host: load your shop, then F2 -> Host via Steam and send a fresh invitation.
   Guest: accept from the main menu. The co-op window closes once the world is ready.

Keep one copy of the DLL, in the path above. Building the project does not deploy
unless deployment is explicitly enabled; installing a package does not edit shop saves.

### Guest interaction help and connection reports

Open **F2 -> Session -> Connection help** (scroll down if needed).

- The progress line distinguishes pending item replies, delayed outgoing updates,
  and refused sends. An idle queue does not prove every shared object agrees.
- Shelf transfer replies carry the affected shelf's current stock. Box repair
  requests resend only the affected box and repeat if no snapshot arrives; ordinary
  transfer recovery no longer needs a full shop refresh for each suppressed box update.

- **Refresh guest objects** requests fresh host state after the shop has loaded.
  It has a 10-second cooldown and waits up to 15 seconds for a full object snapshot.
  Once received, close F2 and retry the interaction. This does not clear pending
  transfers, take ownership from another player, or guarantee every object is repaired.
- Guest notices explain known ownership conflicts, missing host identities, rejected
  box/shelf transfers, and items waiting for confirmation. A transfer waiting over
  five seconds also raises a notice; waiting is not proof the packet was lost.
- **Copy connection report** is available while joining, playing, and disconnected. The
  advanced report includes bounded protocol message counts, join/transfer state, recent
  mod errors and performance spikes, compatibility fingerprints, and relevant settings.
  The host's report also counts guest shelf and box transfers and records why the host
  rejected an item or box possession change. It omits packet and save contents, and
  redacts common IDs and addresses. Reports are created locally on click and copied to
  the clipboard. Review before sharing: free-text errors can still mention player
  names or other personal details. Password/config dumps are not added.
- Reports also include per-peer Steam queue sizes and age, blocked-send duration,
  targeted box repairs, invalid item replies, and queued shared-wallet debits.
  Queue metrics unavailable for a transport are marked unavailable.

Version 1.10.0.1 uses wire 110. Install this exact version on both PCs; the earlier
1.9.2.x packages cannot join it. The connection improvements do not add host approval
prompts. Automatic reconnect is not enabled; an ended session still needs a fresh join.

If refresh times out or items remain blocked, copy the report before restarting the
game, retain both players' logs, and leave/rejoin. These diagnostics cover known co-op
guards; they do not diagnose every vanilla interaction rule or another mod's input hook.

### Two-PC acceptance test (still required)

- Repeat guest shelf pickups while customers buy that same product and a worker
  restocks it. Accepted pickups should stay usable and the shelf count should agree
  after the reply. Try quick consecutive pickups and restocks, including full shelves.
- Change counter checkout/trade settings and move occupied play tables after removing
  an earlier counter/table from the shop. Changes should affect the chosen object on
  both PCs. Start and finish a guest table minigame, then check its remote props again.
- With three players, interrupt one guest's connection briefly while the other
  checks out and moves stock. That guest's blocked sends should not stall the other
  guest's reliable messages. Preserve FIFO for each player; do not expect automatic
  reconnect after a connection ends.
- Check a rejected/partial box transfer with a delayed snapshot. The report should
  show a targeted repair that clears when that box's state arrives, without opening
  extra UI or requiring host approval.
- Try close-together guest purchases and bills in a busy shop. Pending native money
  events must remain reserved until the wallet applies them. A failed purchase should
  not be executed again automatically or play another success sound.

- Let a guest change hair, hair color, and outfit while the host watches. The guest's
  customized appearance should remain visible after the preview or avatar finishes
  starting. Then have the guest spray a smelly customer: the host should see that
  customer become clean and the clean count should advance. Spray until the can is
  empty and confirm no extra cleaning continues.
- Use a recently taken pack after its transfer settles. Any short sync message should
  describe automatic item syncing, never host permission or approval.
- On each PC, hold a partly full box and remove/return packs. Its product/count
  label should show the current quantity even while open, without placing the box.
  Empty it completely: both the count and icon should disappear. Refill it with
  another pack type: the new icon and count should return immediately. Close/open,
  drop/pick up, and store/retrieve it from a rack; stored and hidden remote-held
  boxes should not leave floating labels. Repeat after leaving and rejoining.
- As the guest, buy wall/floor/ceiling decoration, a decoration item, hire a worker,
  and submit a grading job. Each accepted purchase should confirm audibly once.
  Refused purchases should not play a success sound. Existing stock, furniture,
  license, expansion and bill-payment sounds should still work.
- Spray while the other player opens/closes Pause and adjusts SFX volume, including
  zero. Their remote spray sound should follow those settings and stop when spraying
  stops or the sprayer leaves. Repeat with the roles reversed.
- At a register, refresh guest objects after several scans. Already-restored scans
  should stay silent; clicking an unscanned item should still beep normally. Confirm
  the drawer, change and completed-sale sounds during normal checkout on each PC.
- Leave the guest session during the end-of-day money count-up, including with SFX
  muted. The counting loop should not carry into the next scene/session.
- Open the guest's phone before the host opens the day recap. Close the phone:
  the recap should appear once. Repeat with the host continuing to the next day
  while the guest stays on the phone; yesterday's report should appear once in
  phone history, with no old recap opening later. Also request end-of-day as the
  guest while the host has a menu open: the host closing it should open the shared
  recap automatically, with no separate host approval.
- Change table numbers on both PCs close together, including a swap and Clear.
  Verify the final numbers agree, remain unique, and survive save/reload. Try Auto
  on an unnumbered table afterwards. Tournament tables keep the game's usual rules.
- If checkout recovery occurs, verify old change disappears on both PCs and a
  subsequent customer can be served. Keep both logs if a register still resets.
- Run a cash checkout from each PC. Click every coin and bill, verify the other
  player sees the same denomination, then right-click the corresponding drawer
  control to take it back. Also aim at money on the tray and right-click to remove
  that denomination. Check the change total and complete another transaction.
- As the guest, hold an open empty box and return packs from a display rack. Check
  that packs appear inside it before placing it down, and that shelf plus box totals
  stay constant. Repeat with a partly full box, a full box, a different product,
  and an ordinary hand-to-box transfer. Successful shelf-to-box pulls should play
  a pop sound and a short pack flight. An empty box should have no product tag.
  Check the host's view after placement too.
- Run into loose boxes from each PC, then drop/throw and store/retrieve them. Check
  for floor clipping, sustained spinning, and position disagreements. Collision
  tuning reduces violent contacts but does not lock boxes upright; they can still tip.

- After buying/placing a play table, reach 21:00 and press Enter on the guest.
  If customers remain queued, confirm the normal warning. The recap should open
  on both PCs; the guest's Continue/Next Day should reach the next morning. Repeat
  with the host initiating and both players clicking Continue together. Verify one
  day advance and one event fee. Then save/reload and verify table state.
- In the play-table number settings, use Auto as the guest on two different tables.
  Check distinct numbers on both PCs, then move/remove a table and repeat. This
  version addresses the Auto table-number button; other controls need their exact
  screen/button identified if a separate issue persists.
- Watch the guest's sky, outdoor brightness, shadows, and shop lights through
  sunset, 21:00, and the next morning. Rejoin at night to check recovery.
- Equip and spray a deodorant can from each PC. The other player should see the
  can, raised arm, and spray mist and hear nearby spraying. Release the button,
  empty/stow the can, change character, and disconnect; effects must stop.

- Watch customers sit, play cards, stand, and walk away on the guest. Walk far away
  and return; the seated pose should recover. Check customers at registers too.
- Have each player crouch, walk while crouching, stand, and change their character
  preset. The other player should see bent knees and a lowered body. The stock
  character controller has no crouch clip, so this uses a procedural humanoid pose;
  replacement non-humanoid character rigs are not supported by this pose.
- Buy stock (phone and scanner), furniture, and a license as the guest. Check one
  purchase sound on success and none when declined. Pay a bill and buy an expansion;
  each successful action should sound once, without repeating on state refresh.
- Test register scanning, cash drawer, cash/card checkout, and item pickup/place
  sounds. These retain their existing local vanilla paths; they were reviewed but
  have not been verified by listening in a live two-PC session.

- Join from the title, leave, and rejoin at least three times. Check the guest reaches
  the shop and can open the phone, pick up/drop a box, and stock a shelf.
- In a new shop after naming it, have only the guest buy stock and change pack prices.
  Verify those starting tasks advance on both PCs. Continue with guest pickups,
  stocking, opening a pack, entering a register, and placing/removing a displayed card.
  Repeat an action from each PC, then save on the host and rejoin to check persistence.
  Shared progress belongs to this shop; the guest's separate solo save is unchanged.
- Open Session -> Connection help. Request a refresh, confirm snapshot receipt, close
  F2, and retry a pickup and shelf transfer. Confirm rapid clicks cannot spam requests.
- Have both players pick up the same box and check the losing guest sees an ownership
  notice. Under latency, try using a pending item and check the waiting notice.
- Copy a report while downloading, during play, and after leaving or a failed join.
  Paste into a text editor; confirm the last session details survive and downloaded
  byte totals remain correct after the buffers have been consumed.
- Try an expired invitation, a full lobby, and cancelling a join followed by a fresh
  invitation. Failed Steam entries should show a reason; an unanswered lobby request
  should stop after 30 seconds. A late reply must not connect the cancelled attempt.
- Set different currencies and attempt a join: it should be rejected before the world
  download. Match currencies and rejoin successfully. Repeat checkout with both players
  using a currency with a conversion rate, such as Japanese yen.
- Change either player's currency during co-op: that session should end with a currency
  message. Choose matching settings and rejoin before continuing checkout. The mod does
  not automatically change either player's currency preference.
- On the guest, change gender/preset, hair, clothing, and sliders quickly. Check the
  host sees the final choice; rejoin and verify both characters' appearances again.
- With a visible hairstyle selected, change its color from black/white to bright red
  and blue. Verify the actual hair mesh changes in the guest preview and on the host.
  Give different hair slots different colors and change hairstyles; confirm one slot
  does not repaint the others. Repeat with male/female models and after rejoining.
  Scalp-only hairstyles have no separate hair mesh to tint.
- Serve several cash and card customers as the guest. Include rapid scanning,
  taking change back, a wrong card amount followed by the correct one, and switching
  between host and guest at the same counter. Verify the shared balance changes once.
- Repeat after the next in-game day to cover NPC pool reuse. Check another customer
  never inherits the previous customer's scans, payment, or change.
- Try a larger shop and a delayed connection. If a join fails, record the displayed
  stage and retain both players' newest `BepInEx/CardShopCoop_*.log` plus
  `BepInEx/LogOutput.log`. A timeout diagnoses a stalled stage; it does not repair
  a corrupt save, incompatible content mod, or unavailable Steam connection.

This build has not had a live two-PC playtest. Broader guest item physics, third-party
model mods, and all other Nexus reports are not certified fixed.

### Roll back

Close the game on both PCs, remove the custom DLL, and restore one backed-up official
DLL per PC. Both players must use the same version. Preserve the save backup until
you are satisfied with the test; installing or removing this package does not itself
rewrite your shop saves.

### Reproduce the checks

With .NET 9 SDK installed and `CARDSHOP_GAMEPATH` pointing to the game folder:

```powershell
dotnet build src/CardShopCoop/CardShopCoop.csproj -c Release
dotnet run --project tests/CustomCoop/CustomCoop.csproj -c Release
dotnet run --project tests/ItemTransfers/ItemTransfers.csproj -c Release
dotnet run --project tests/ShelfBoxPull/ShelfBoxPull.csproj -c Release
dotnet run --project tests/BoxLabels/BoxLabels.csproj -c Release
dotnet run --project tests/PresentationWire/PresentationWire.csproj -c Release
dotnet run --project tests/SteamLobby/SteamLobby.csproj -c Release
dotnet format src/CardShopCoop/CardShopCoop.csproj whitespace --verify-no-changes --no-restore
```

Only the mod DLL, documentation, and MIT license belong in the distributable.
Game assemblies and the locally decompiled game code are excluded.

---

[![Discord](https://img.shields.io/badge/Discord-join%20the%20shop-2ec484)](https://discord.gg/eNswvvTYbQ)

**True co-op multiplayer for TCG Card Shop Simulator.** Run the shop together — shared
money, XP, collection, and customers — over Steam (invites or a public lobby browser)
or LAN. Built mod-first: it syncs by game IDs and plays nice with the PTCGO / Enhanced
Prefab Loader content-mod stack, including automatic card-database alignment between
players.

BepInEx 5 plugin, Unity 2021.3 Mono, no game assets redistributed.

Most of the 1.2.0 update is [Meepen](https://github.com/meepen)'s work. Thanks for the continued development, fixes, and testing!

## Features

- **Steam lobbies**: friends-list invites, or a browsable/searchable public lobby list
  with optional passwords. LAN/TCP fallback (port 27886). 2 players primary; extra
  joiners supported via host relay.
- **One shop, one truth** (host-authoritative): money, XP, level, fame, the card
  collection (including graded cards), item and card prices, shelf stock, card display
  walls, loose boxes (carry them, throw them, trash them), placed furniture, licenses,
  bills, room expansions, decorations, signs, tournaments, grading, the daily market,
  and the end-of-day report are all shared.
- **Both players can work**: the joiner serves the register through the vanilla interaction
  flow (click the counter to man it, click items/cards to scan, take payment, give change,
  and press Space to finish),
  runs customer trade-ins/sell-ins through the game's real trade screen, restocks,
  prices, orders stock and furniture, hires staff, pays bills, and buys licenses and
  expansions — everything lands in the host's real simulation and echoes back.
- **Live world**: customers and workers mirrored as motion-smoothed puppets
  (snapshot-interpolated, not jittery extrapolation), full day/night cycle and lighting
  sync, avatars with real carried items (boxes with product, card fans, binders).
- **Allow NSFW**: when off, the Nude wardrobe option is hidden and fully nude players
  appear in the game's random clothed customer look; when on, nude appearances are allowed.
- **Mod-stack aware**: plugin-set and card-ID-registry parity checks at join with
  readable rejections, automatic `enum_values.json` sync (backup + install + "restart
  and rejoin"), product-catalog diffing with plain-language warnings, refunds when an
  ordered product doesn't exist on the host.
- **Joiners risk nothing**: the joiner receives the host's save at join, plays in a
  dedicated scratch slot, and never writes their own saves.

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx) (5.4.23 x64) into the game
   folder — most modded installs already have it.
2. Drop `CardShopCoop.dll` into `BepInEx/plugins/` on **both** PCs.
3. Both players must run the **same CardShopCoop version** and the **same mod set**
   (including content data packs) — the join handshake tells you exactly what differs
   if not.
4. In game, press **F2** for the co-op window. Host: load your save, click Host.
   Friend: Join via Steam invite, the lobby browser, or LAN IP.

## Repo layout

- `src/CardShopCoop/` — plugin source. Build: `dotnet build -c Release`
  (deployment is opt-in; use `-p:Deploy=true` only when you want the DLL copied into the
  game's plugins directory).
  The game install path (`GamePath`) is resolved by `Directory.Build.props`:
  point it at your install by copying `Directory.Build.user.props.example` to
  `Directory.Build.user.props` (git-ignored), or set the `CARDSHOP_GAMEPATH`
  environment variable. `dotnet build -p:GamePath=...` overrides either for a
  one-off/CI build. Don't edit the csproj.
- `tools/Decomp/` — regenerates the decompiled game-assembly reference locally
  (ILSpy; the output is not part of this repo).
- Ready-to-install builds: see [Releases](https://github.com/DeliriumPulse/CardShopCoop/releases)
  or the Nexus page.

## Architecture notes

- **Host-authoritative everywhere**: the host runs the only real simulation; the client
  suppresses its own customers/workers/day-end via Harmony and mirrors state. Client
  actions forward as ops the host executes through vanilla code paths, then authoritative
  state echoes back (hash-gated snapshot-diff engines with staggered timers).
- **Two network lanes**: reliable ordered frames for state, unreliable no-delay for
  15 Hz positions and 8 Hz NPC batches (chunked under Steam's 1200-byte datagram limit).
  Remote motion renders ~150 ms behind on a snapshot ring buffer.
- **Identity over indexes**: anything that crosses the wire is keyed by item identity
  (type + size + name), never by list position — content mods can order their
  registries differently per machine.
- Game gotchas that cost us dearly (see `Patches/GamePatches.cs` and git history):
  dead statics (`CGameManager.Player`), auto-creating `CSingleton<T>.Instance`,
  `SpawnItem` being a save-loader not an adder, price tags living in separate canvas
  groups, and raw-`itemType`-indexed tables ~200k entries long under content mods.

## License

MIT — see [LICENSE](LICENSE).
