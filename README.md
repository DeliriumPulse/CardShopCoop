# CardShopCoop

LAN co-op mod for **TCG Card Shop Simulator** (BepInEx 5, Unity 2021.3 Mono).
Built from scratch for a modded install (PTCGO / Pokemon overhaul + EPL stack) —
it syncs by game IDs and never touches card content, so content mods keep working.

## How it works

- **Host-authoritative**: the host runs the only real simulation. The joiner receives
  a byte-perfect copy of the host's save (base JSON + all per-slot mod sidecar files,
  slot-renamed) at join time and loads it through the game's own load path into a
  dedicated co-op slot (default 7) — the joiner's own saves are never written.
- **Transport**: dependency-free TCP (port 27886), length-prefixed frames, background
  reader + keepalive threads (immune to Unity main-thread freezes during scene loads),
  30s timeout.
- **Avatars**: the game's customer prefab, dressed via `Customer.RandomizeCharacterMesh()`
  (the game's own wardrobe pipeline), then stripped of all AI/physics same-call so it
  never simulates. Driven by 12 Hz position/yaw/speed/hold packets; walk cycle via the
  `MoveSpeed` animator param; carry pose via `IsHoldingBox` + a prop cube.
- **World sync**: snapshot-diff engine over shelf/warehouse compartments
  (`(shelfIdx, compartmentIdx) -> (itemType, count)` at 0.75s cadence). Host diffs are
  authoritative broadcasts; client diffs (the joiner's own restocking) are requests the
  host applies and echoes. No per-interaction Harmony patches needed.
- **Economy/time**: host wallet broadcast through the game's own event bus
  (`CEventPlayer_SetCoin`), item price table sync, day/time mirrored into `LightManager`.
- **Client sim suppression** (Harmony): customers, workers and day-end events are
  disabled on the joiner; all joiner saves reroute to the co-op slot.

## Repo layout

- `src/CardShopCoop/` — plugin source. Build: `dotnet build -c Release`
  (auto-deploys the DLL into the game's `BepInEx/plugins/CardShopCoop/`; pass
  `/p:SkipDeploy=true` while the game is running).
- `dist/` — `PLAY_GUIDE.md`, `Stage-ModsToUSB.ps1` (dad's PC), `Setup-SonPC.ps1` (son's PC).
- `decompiled/` — ILSpy output of the game's `Assembly-CSharp.dll` (build 22936874),
  the reference for every hook. Not redistributable; local use only.
- `backup/` — pre-co-op save backup.

## Testing without a second PC

`Card Shop Simulator.exe -coopautohost=0` and a second instance with
`-coopautojoin=127.0.0.1` (requires `steam_appid.txt` in the game folder for direct-exe
launch). Per-process logs: `BepInEx/CardShopCoop_<pid>.log`.

## Gotchas discovered (for future maintenance)

- `CGameManager.Player` and `InteractionPlayerController.m_Instance` are **dead statics**
  — declared, never assigned. Resolve the player via `FindObjectOfType`.
- `CSingleton<T>.Instance` **auto-creates** an empty object when none exists — never
  call it from the title screen for scene-bound managers.
- Save files are plain JsonUtility JSON (`savedGames_Release{slot}.json`); mods keep
  per-slot sidecars in `LocalLow/OPNeonGames/Card Shop Simulator/<Mod>/<Name>_{slot}.*`;
  EPL's `enum_values.json` is a machine-global custom-item ID registry — never overwrite
  an existing, differing copy.
- Two instances share one save dir when loopback-testing: the client save-guard patch is
  what makes that safe.

## Roadmap

- v0.3: mirror host customers/workers as puppets on the client; sync loose/stored boxes
  and card shelves; forward client restock purchases to the host wallet.
- v0.4: pack-opening and card-table visibility; Steam-lobby transport option
  (game already ships Steamworks.NET + Heathen) for play over the internet.
