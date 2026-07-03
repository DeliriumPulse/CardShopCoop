# CardShopCoop — Remote Setup (playing from another town)

You'll join over Steam — no IPs, no router setup. You need: your own Steam copy of
TCG Card Shop Simulator, to be Steam friends with the host, and a mod setup that
**matches the host's exactly** (the mod checks this and will tell you if it differs).

## Stage 1 — quick wire test (15 minutes, no Pokemon mods yet)

Prove the connection works before downloading gigabytes.

1. Install TCG Card Shop Simulator from Steam. Launch once, quit.
2. Install BepInEx: get "BepInExPack" for TCG Card Shop Simulator from Thunderstore
   (thunderstore.io → TCG Card Shop Simulator → BepInExPack). Extract into the game
   folder so `winhttp.dll` sits next to `Card Shop Simulator.exe`. Launch once, quit.
3. From this kit, copy `CardShopCoop.dll` into
   `<game>\BepInEx\plugins\CardShopCoop\` (create the folder).
4. **The host must run the same minimal setup for this test** (he has a script for it)
   and host a throwaway new-game save — not the Pokemon shop.
5. Host: F2 → Host via Steam → Invite friend. You: accept the invite at the game's
   main menu (or even with the game closed — Steam will launch it). The shop
   downloads (~a minute) and you're in.

If Stage 1 works, the netcode is proven — everything after is just matching mods.

## Stage 2 — the full Pokemon shop

Your mod set must match the host's (~40 plugins + the Pokemon content packs).
Two ways:

- **Easiest**: the host uploads his `BepInEx` folder to a cloud drive overnight
  (it's large — tens of GB with the HD art) and you drop it into your game folder.
- **Or download-it-yourself** from Nexus (nexusmods.com → TCG Card Shop Simulator):
  the PTCGO (Pokemon TCG Overhaul) family — expansions (mod 854), theme decks (856),
  collectibles (870), shop textures/statues (872), the IitzSamurai compatibility
  patch (968) — plus Enhanced Prefab Loader and the host's quality-of-life mods.
  Versions must match the host's; coordinate on Discord while you do it.

Then, either way:

6. Just join. If your custom-card database differs from the host's, the mod
   **syncs it automatically** (your old file is backed up beside it in the
   `PrefabLoader` folder) and asks you to restart the game — restart, join
   again, done. If the mod refuses you for any other reason, it says exactly
   what differs.

> Heads-up if you have your own modded SOLO saves: the sync aligns custom-card
> IDs with the host's, which can re-shuffle how modded cards display in your own
> solo world. The backup (`enum_values.json.coopbak-<date>`) restores it —
> or set `AutoSyncCardDatabase = false` in the mod's config to handle it yourself.

## Good to know

- Your own single-player saves are never touched — the co-op world autosaves to a
  separate slot on your PC.
- The join download runs over Steam's relay: expect ~30–60 seconds.
- If anything fails, send the host the newest
  `<game>\BepInEx\CardShopCoop_<number>.log` — it records exactly where things stopped.
