# CardShopCoop

**True co-op multiplayer for TCG Card Shop Simulator.** Run the shop together — shared
money, XP, collection, and customers — over Steam (invites or a public lobby browser)
or LAN. Built mod-first: it syncs by game IDs and plays nice with the PTCGO / Enhanced
Prefab Loader content-mod stack, including automatic card-database alignment between
players.

BepInEx 5 plugin, Unity 2021.3 Mono, no game assets redistributed.

## Features

- **Steam lobbies**: friends-list invites, or a browsable/searchable public lobby list
  with optional passwords. LAN/TCP fallback (port 27886). 2 players primary; extra
  joiners supported via host relay.
- **One shop, one truth** (host-authoritative): money, XP, level, fame, the card
  collection (including graded cards), item and card prices, shelf stock, card display
  walls, loose boxes (carry them, throw them, trash them), placed furniture, licenses,
  bills, room expansions, decorations, signs, tournaments, grading, the daily market,
  and the end-of-day report are all shared.
- **Both players can work**: the joiner serves the register (click-to-scan or hold V),
  runs customer trade-ins/sell-ins through the game's real trade screen, restocks,
  prices, orders stock and furniture, hires staff, pays bills, and buys licenses and
  expansions — everything lands in the host's real simulation and echoes back.
- **Live world**: customers and workers mirrored as motion-smoothed puppets
  (snapshot-interpolated, not jittery extrapolation), full day/night cycle and lighting
  sync, avatars with real carried items (boxes with product, card fans, binders).
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
  (auto-deploys into the game's plugins; pass `/p:SkipDeploy=true` while the game runs).
  Set `<GamePath>` in the csproj to your install.
- `dist/` — play guide, remote-friend setup kit, release packaging.
- `decompiled/` (not in repo) — ILSpy output of the game assembly used as hook
  reference; regenerate locally with `tools/Decomp`.

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
